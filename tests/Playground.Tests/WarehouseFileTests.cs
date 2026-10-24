using System.Text.Json;
using Fulfillment.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Playground.Contracts;
using Playground.Infrastructure;
using Warehouse.Simulator;
using Xunit;

namespace Playground.Tests;

public sealed class WarehouseFileTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private sealed class SimulatedCrash : Exception;

    [Fact]
    public async Task Writer_simulator_and_http_lookup_exchange_only_business_fields()
    {
        using var data = new FulfillmentData();
        var message = FulfillmentTests.Message();
        var fulfillment = data.Store();
        fulfillment.Accept(message);
        await using var host = await CourierHost.Start(data);
        Assert.True(await host.Processor(fulfillment).ProcessAsync(message.OrderId, CancellationToken.None));
        var reference = fulfillment.Get(message.OrderId)!.WarehouseReference;
        Writer(data).WritePending();
        Assert.Empty(fulfillment.UnsentFiles());
        var requestPath = Assert.Single(Files(data, "requests"));
        var request = WireJson.Read<WarehouseRequest>(WarehouseFiles.Read(requestPath));
        Assert.Equal(reference, request.WarehouseReference);
        AssertFields(requestPath, "schemaVersion", "warehouseReference", "items", "shippingAddress", "courierCode", "estimatedDays");
        var simulator = Processor(data);
        simulator.ReadRequests(Now);
        Assert.Empty(Files(data, "requests"));
        Assert.Single(Files(data, "processed/requests"));
        simulator.WriteResponses(Now);
        var responsePath = Assert.Single(Files(data, "responses"));
        var response = WireJson.Read<WarehouseResponse>(WarehouseFiles.Read(responsePath));
        Assert.Equal(reference, response.WarehouseReference);
        AssertFields(responsePath, "schemaVersion", "warehouseReference", "shipmentId", "shippedAt");
        using var lookup = await host.Client.GetAsync("/internal/v1/warehouse-requests/" + reference);
        var mapping = WireJson.Read<WarehouseMappingResponse>(await lookup.Content.ReadAsStringAsync());
        Assert.Equal(message.OrderId, mapping.OrderId);
        Assert.Equal(mapping, data.Store().Lookup(reference));
    }

    [Theory]
    [InlineData(FilePublicationStage.Flushed)]
    [InlineData(FilePublicationStage.Renamed)]
    public void Request_writer_recovers_flush_or_rename_before_database_commit(FilePublicationStage crashAt)
    {
        using var data = new FulfillmentData();
        var message = Seed(data);
        var reference = data.Store().Get(message.OrderId)!.WarehouseReference;
        Assert.Throws<SimulatedCrash>(() => Writer(data).WritePending(stage => { if (stage == crashAt) throw new SimulatedCrash(); }));
        Assert.Single(data.Store().UnsentFiles());
        var requests = Folder(data, "requests");
        if (crashAt == FilePublicationStage.Flushed)
        {
            Assert.Empty(Files(data, "requests"));
            Assert.Single(Directory.GetFiles(requests, "*.tmp-*"));
            Processor(data).ReadRequests(Now);
            Assert.Equal(0, data.Count("Warehouse.Simulator", "SELECT COUNT(*) FROM warehouse_shipments"));
        }
        using (WarehouseFiles.AcquireWriterLease(requests, "request")) WarehouseFiles.RemoveOrphanTemps(requests, "request");
        Writer(data).WritePending();
        Assert.Empty(data.Store().UnsentFiles());
        Assert.Empty(Directory.GetFiles(requests, "*.tmp-*"));
        var file = Assert.Single(Files(data, "requests"));
        Assert.Equal(reference, WireJson.Read<WarehouseRequest>(WarehouseFiles.Read(file)).WarehouseReference);
    }

    [Fact]
    public void Simulator_can_consume_before_writer_marks_sent_and_replay_still_has_one_shipment()
    {
        using var data = new FulfillmentData();
        Seed(data);
        Assert.Throws<SimulatedCrash>(() => Writer(data).WritePending(stage => { if (stage == FilePublicationStage.Renamed) throw new SimulatedCrash(); }));
        var processor = Processor(data);
        processor.ReadRequests(Now);
        processor.WriteResponses(Now);
        var responsePath = Assert.Single(Files(data, "responses"));
        var original = WarehouseFiles.Read(responsePath);
        File.Delete(responsePath); // A downstream consumer has already acquired this copy.
        Writer(data).WritePending(); // Recreates the request from the uncommitted writer outbox.
        processor = Processor(data);
        processor.ReadRequests(Now.AddMinutes(1));
        processor.WriteResponses(Now.AddMinutes(1));
        Assert.Equal(original, WarehouseFiles.Read(Assert.Single(Files(data, "responses"))));
        Assert.Equal(1, data.Count("Warehouse.Simulator", "SELECT COUNT(*) FROM warehouse_shipments"));
        Assert.Equal(2, Files(data, "processed/requests").Length);
    }

    [Theory]
    [InlineData(WarehouseInputStage.Accepted)]
    [InlineData(WarehouseInputStage.Archived)]
    public void Simulator_recovers_after_durable_acceptance_or_input_archive(WarehouseInputStage crashAt)
    {
        using var data = new FulfillmentData();
        Seed(data);
        Writer(data).WritePending();
        Assert.Throws<SimulatedCrash>(() => Processor(data).ReadRequests(Now, stage => { if (stage == crashAt) throw new SimulatedCrash(); }));
        var original = Assert.Single(Store(data).Pending()).ResponseJson;
        var restarted = Processor(data);
        restarted.ReadRequests(Now.AddMinutes(1));
        restarted.WriteResponses(Now.AddMinutes(1));
        Assert.Equal(original, WarehouseFiles.Read(Assert.Single(Files(data, "responses"))));
        Assert.Empty(Store(data).Pending());
        Assert.Empty(Files(data, "requests"));
        Assert.Single(Files(data, "processed/requests"));
        Assert.Equal(1, data.Count("Warehouse.Simulator", "SELECT COUNT(*) FROM warehouse_shipments"));
    }

    [Theory]
    [InlineData(FilePublicationStage.Flushed)]
    [InlineData(FilePublicationStage.Renamed)]
    public void Response_publication_recovers_with_identical_business_identity(FilePublicationStage crashAt)
    {
        using var data = new FulfillmentData();
        Seed(data);
        Writer(data).WritePending();
        var processor = Processor(data);
        processor.ReadRequests(Now);
        var original = Assert.Single(Store(data).Pending()).ResponseJson;
        Assert.Throws<SimulatedCrash>(() => processor.WriteResponses(Now, stage => { if (stage == crashAt) throw new SimulatedCrash(); }));
        Assert.Single(Store(data).Pending());
        var directory = Folder(data, "responses");
        using (WarehouseFiles.AcquireWriterLease(directory, "response")) WarehouseFiles.RemoveOrphanTemps(directory, "response");
        Processor(data).WriteResponses(Now.AddMinutes(1));
        Assert.Equal(original, WarehouseFiles.Read(Assert.Single(Files(data, "responses"))));
        Assert.Empty(Store(data).Pending());
        Assert.Empty(Directory.GetFiles(directory, "*.tmp-*"));
    }

    [Fact]
    public void Duplicate_files_and_duplicate_response_profile_preserve_identity_across_restarts()
    {
        using var data = new FulfillmentData();
        var message = Seed(data);
        Writer(data).WritePending();
        var source = Assert.Single(Files(data, "requests"));
        var json = WarehouseFiles.Read(source);
        File.WriteAllText(Path.Combine(Folder(data, "requests"), "copy.json"), json);
        var reference = data.Store().Get(message.OrderId)!.WarehouseReference;
        var simulation = new WarehouseSimulation("duplicate-warehouse-response", 0, reference);
        var processor = Processor(data, simulation);
        processor.ReadRequests(Now);
        processor.WriteResponses(Now);
        var responses = Files(data, "responses");
        Assert.Equal(2, responses.Length);
        var original = Assert.Single(responses.Select(WarehouseFiles.Read).Distinct());
        Assert.Equal(1, data.Count("Warehouse.Simulator", "SELECT COUNT(*) FROM warehouse_shipments"));
        foreach (var file in responses) File.Delete(file);
        // Same filename and same request after restart must reproduce the same result.
        File.WriteAllText(source, json);
        processor = Processor(data, simulation);
        processor.ReadRequests(Now.AddHours(1));
        processor.WriteResponses(Now.AddHours(1));
        Assert.Equal(2, Files(data, "responses").Length);
        Assert.All(Files(data, "responses"), path => Assert.Equal(original, WarehouseFiles.Read(path)));
        Assert.Equal(3, Files(data, "processed/requests").Length);
    }

    [Fact]
    public void Malformed_incomplete_and_conflicting_inputs_are_quarantined_without_stopping_valid_work()
    {
        using var data = new FulfillmentData();
        Seed(data);
        Writer(data).WritePending();
        var source = Assert.Single(Files(data, "requests"));
        var request = WireJson.Read<WarehouseRequest>(WarehouseFiles.Read(source));
        Processor(data).ReadRequests(Now);
        var directory = Folder(data, "requests");
        File.WriteAllText(Path.Combine(directory, "partial.json.tmp-test"), "{");
        File.WriteAllText(Path.Combine(directory, "broken.json"), "{");
        File.WriteAllText(Path.Combine(directory, "wrong-version.json"), WireJson.Write(request).Replace("\"schemaVersion\":1", "\"schemaVersion\":2"));
        File.WriteAllText(Path.Combine(directory, "conflict.json"), WireJson.Write(request with { EstimatedDays = 99 }));
        File.WriteAllText(Path.Combine(directory, "extra.json"), WireJson.Write(request).Insert(1, "\"unknown\":1,"));
        File.WriteAllText(Path.Combine(directory, "valid.json"), WireJson.Write(request with { WarehouseReference = "../../outside" }));
        Processor(data).ReadRequests(Now.AddMinutes(1));
        Processor(data).WriteResponses(Now.AddMinutes(1));
        Assert.Equal(4, Files(data, "errors/requests").Length);
        Assert.Equal(2, Files(data, "responses").Length);
        Assert.Equal(2, data.Count("Warehouse.Simulator", "SELECT COUNT(*) FROM warehouse_shipments"));
        Assert.True(File.Exists(Path.Combine(directory, "partial.json.tmp-test")));
        Assert.All(Files(data, "responses"), path => Assert.Equal(Folder(data, "responses"), Path.GetDirectoryName(path)));
        // Same bad source filename on a later cycle never overwrites old quarantine evidence.
        File.WriteAllText(Path.Combine(directory, "broken.json"), "{");
        Processor(data).ReadRequests(Now.AddMinutes(2));
        Assert.Equal(5, Files(data, "errors/requests").Length);
    }

    [Fact]
    public void Delay_and_missing_response_are_selected_and_durable_without_blocking_other_requests()
    {
        using var data = new FulfillmentData();
        var delayed = Seed(data);
        var normal = Seed(data);
        Writer(data).WritePending();
        var reference = data.Store().Get(delayed.OrderId)!.WarehouseReference;
        var slow = new WarehouseSimulation("slow-warehouse", 6000, reference);
        var processor = Processor(data, slow);
        processor.ReadRequests(Now);
        processor.WriteResponses(Now.AddSeconds(5));
        var first = WireJson.Read<WarehouseResponse>(WarehouseFiles.Read(Assert.Single(Files(data, "responses"))));
        Assert.Equal(data.Store().Get(normal.OrderId)!.WarehouseReference, first.WarehouseReference);
        processor = Processor(data, new("missing-warehouse-response", 0, reference));
        processor.WriteResponses(Now.AddDays(1));
        Assert.Single(Store(data).Pending());
        Assert.Single(Files(data, "responses"));
        // Delay uses original persisted acceptance, not restart time.
        Processor(data, slow).WriteResponses(Now.AddSeconds(6));
        Assert.Equal(2, Files(data, "responses").Length);
        Assert.Empty(Store(data).Pending());
    }

    [Fact]
    public void Quarantine_failure_is_bounded_persistent_and_does_not_block_valid_inputs()
    {
        using var data = new FulfillmentData();
        Seed(data);
        Writer(data).WritePending();
        var bad = Path.Combine(Folder(data, "requests"), "bad.json");
        File.WriteAllText(bad, "{");
        File.WriteAllText(Path.Combine(data.Settings.Warehouse, "errors"), "blocked-directory");
        for (var n = 0; n < 3; n++) Processor(data).ReadRequests(Now.AddSeconds(n * 10));
        Assert.Equal(3, Store(data).Failure("input:bad.json")!.Attempts);
        Assert.True(File.Exists(bad));
        Assert.Single(Files(data, "processed/requests"));
        File.Delete(Path.Combine(data.Settings.Warehouse, "errors"));
        Processor(data).ReadRequests(Now.AddMinutes(1));
        Assert.True(File.Exists(bad)); // Restart/time alone does not reset exhaustion.
        Store(data).ClearFailure("input:bad.json"); // Explicit operator recovery after correcting the cause.
        Processor(data).ReadRequests(Now.AddMinutes(2));
        Assert.False(File.Exists(bad));
        Assert.Single(Files(data, "errors/requests"));
    }

    [Fact]
    public void Output_io_failure_is_bounded_and_durable_response_can_be_resumed()
    {
        using var data = new FulfillmentData();
        Seed(data);
        Writer(data).WritePending();
        var processor = Processor(data);
        processor.ReadRequests(Now);
        var pending = Assert.Single(Store(data).Pending());
        File.WriteAllText(Path.Combine(data.Settings.Warehouse, "responses"), "blocked-directory");
        for (var n = 0; n < 3; n++) Processor(data).WriteResponses(Now.AddSeconds(n * 10));
        Assert.Equal(3, Store(data).Failure("output:" + pending.Reference)!.Attempts);
        Assert.Single(Store(data).Pending());
        File.Delete(Path.Combine(data.Settings.Warehouse, "responses"));
        Processor(data).WriteResponses(Now.AddMinutes(1));
        Assert.Empty(Files(data, "responses"));
        Store(data).ClearFailure("output:" + pending.Reference);
        Processor(data).WriteResponses(Now.AddMinutes(2));
        Assert.Equal(pending.ResponseJson, WarehouseFiles.Read(Assert.Single(Files(data, "responses"))));
    }

    [Fact]
    public void Conflicting_outputs_are_not_overwritten_and_other_work_continues()
    {
        using var data = new FulfillmentData();
        var bad = Seed(data);
        Seed(data);
        var reference = data.Store().Get(bad.OrderId)!.WarehouseReference;
        var target = Path.Combine(Folder(data, "requests"), WarehouseFiles.Name("request", reference));
        File.WriteAllBytes(target, [0xff, 0xfe, 0xff]);
        Writer(data).WritePending();
        Assert.Equal(new byte[] { 0xff, 0xfe, 0xff }, File.ReadAllBytes(target));
        Assert.Single(data.Store().UnsentFiles());
        Assert.False(data.Store().CanTryFile(reference));
        Assert.Equal(2, Files(data, "requests").Length);
        Processor(data).ReadRequests(Now);
        var pending = Assert.Single(Store(data).Pending());
        var response = Path.Combine(Folder(data, "responses"), WarehouseFiles.Name("response", pending.Reference));
        File.WriteAllText(response, "response-evidence");
        Processor(data).WriteResponses(Now);
        Assert.Equal("response-evidence", File.ReadAllText(response));
        Assert.Equal("output_conflict", Store(data).Failure("output:" + pending.Reference)!.LastError);
        Assert.Single(Store(data).Pending());
    }

    [Fact]
    public void Writer_lease_prevents_cleaning_an_active_writer_and_cleanup_is_scoped()
    {
        using var data = new FulfillmentData();
        var directory = Folder(data, "requests");
        var own = Path.Combine(directory, "request-safe.json.tmp-orphan");
        var unrelated = Path.Combine(directory, "unrelated.tmp-orphan");
        File.WriteAllText(own, "partial");
        File.WriteAllText(unrelated, "untouched");
        using (WarehouseFiles.AcquireWriterLease(directory, "request"))
        {
            Assert.Throws<IOException>(() => WarehouseFiles.AcquireWriterLease(directory, "request"));
            WarehouseFiles.RemoveOrphanTemps(directory, "request");
        }
        Assert.False(File.Exists(own));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task Background_polling_workers_publish_and_archive_without_manual_cycles()
    {
        using var data = new FulfillmentData();
        var message = Seed(data);
        using var writer = new WarehouseRequestWorker(Writer(data), data.Settings, NullLogger<WarehouseRequestWorker>.Instance);
        using var simulator = new WarehouseWorker(Processor(data), data.Settings, NullLogger<WarehouseWorker>.Instance);
        await writer.StartAsync(CancellationToken.None);
        await simulator.StartAsync(CancellationToken.None);
        try
        {
            await FulfillmentTests.Until(() => Files(data, "responses").Length == 1 && data.Store().UnsentFiles().Count == 0);
            var response = WireJson.Read<WarehouseResponse>(WarehouseFiles.Read(Assert.Single(Files(data, "responses"))));
            Assert.Equal(message.OrderId, data.Store().Lookup(response.WarehouseReference)!.OrderId);
            Assert.Single(Files(data, "processed/requests"));
        }
        finally
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await writer.StopAsync(deadline.Token);
            await simulator.StopAsync(deadline.Token);
        }
        Assert.True(writer.ExecuteTask!.IsCompletedSuccessfully);
        Assert.True(simulator.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Request_writer_io_budget_survives_restart_without_losing_the_outbox()
    {
        using var data = new FulfillmentData();
        var message = Seed(data);
        var reference = data.Store().Get(message.OrderId)!.WarehouseReference;
        Directory.CreateDirectory(data.Settings.Warehouse);
        var blocked = Path.Combine(data.Settings.Warehouse, "requests");
        File.WriteAllText(blocked, "blocked-directory");
        await FulfillmentTests.Until(() =>
        {
            Writer(data).WritePending();
            return data.Count("Fulfillment.Service", "SELECT COALESCE(MAX(attempts),0) FROM warehouse_request_file_failures") == 3;
        });
        Assert.Single(data.Store().UnsentFiles());
        File.Delete(blocked);
        Writer(data).WritePending();
        Assert.Empty(Files(data, "requests"));
        Assert.False(data.Store().CanTryFile(reference));
        Assert.Single(data.Store().UnsentFiles());
    }
    private static FulfillOrder Seed(FulfillmentData data)
    {
        var message = FulfillmentTests.Message();
        var store = data.Store();
        store.Accept(message);
        store.Complete(message.OrderId, new(message.OrderId, "LOCAL-SIM", 2));
        return message;
    }
    private static SimulatorStore Store(FulfillmentData data)
    {
        var store = new SimulatorStore(data.Settings);
        store.Initialize();
        return store;
    }
    private static WarehouseRequestWriter Writer(FulfillmentData data) => new(data.Store(), data.Settings, NullLogger<WarehouseRequestWriter>.Instance);
    private static WarehouseProcessor Processor(FulfillmentData data, WarehouseSimulation? simulation = null) =>
        new(Store(data), data.Settings, simulation ?? new("happy-path", 0, null), NullLogger<WarehouseProcessor>.Instance);
    private static string Folder(FulfillmentData data, string relative)
    {
        var directory = Path.Combine(data.Settings.Warehouse, relative);
        Directory.CreateDirectory(directory);
        return directory;
    }
    private static string[] Files(FulfillmentData data, string relative) => Directory.GetFiles(Folder(data, relative), "*.json");
    private static void AssertFields(string path, params string[] fields)
    {
        using var document = JsonDocument.Parse(WarehouseFiles.Read(path));
        Assert.Equal(fields.Order(), document.RootElement.EnumerateObject().Select(p => p.Name).Order());
    }
}