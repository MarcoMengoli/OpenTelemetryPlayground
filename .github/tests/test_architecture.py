import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('architecture', Path(__file__).parents[1] / 'scripts' / 'check_architecture.py')
architecture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(architecture)


class ArchitectureGuardTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.write('src/Example/Example.csproj', '<Project Sdk="Microsoft.NET.Sdk" />')
        self.write('src/Example/obj/project.assets.json', json.dumps({'libraries': {'Serilog/4.0.0': {'type': 'package'}}}))

    def write(self, name, text):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding='utf-8')

    def test_runtime_diagnostics_and_documented_constraints_are_allowed(self):
        self.write('src/Example/App.cs', 'using System.Diagnostics; var watch = Stopwatch.StartNew();')
        self.write('AGENTS.md', 'Niente OpenTelemetry, Aspire, ActivitySource, Meter o TraceId.')
        self.assertEqual([], architecture.check(self.root, True))

    def test_direct_and_central_dependencies_are_rejected(self):
        for package in ('OpenTelemetry', 'OpenTelemetry.Exporter.Console', 'Aspire.Hosting'):
            with self.subTest(package=package):
                self.write('Directory.Packages.props', f'<Project><ItemGroup><PackageVersion Include="{package}" Version="1.0.0" /></ItemGroup></Project>')
                self.assertTrue(architecture.check(self.root))

    def test_transitive_dependency_is_rejected(self):
        self.write('src/Example/obj/project.assets.json', json.dumps({'libraries': {'OpenTelemetry.Api/1.0.0': {'type': 'package'}}}))
        self.assertTrue(architecture.check(self.root, True))

    def test_missing_base_or_restore_is_not_success(self):
        (self.root / 'src/Example/obj/project.assets.json').unlink()
        self.assertTrue(architecture.check(self.root, True))
        (self.root / 'src/Example/Example.csproj').unlink()
        self.assertTrue(architecture.check(self.root))

    def test_custom_instrumentation_is_rejected(self):
        self.write('src/Example/App.cs', 'var source = new System.Diagnostics.ActivitySource("example");')
        self.assertTrue(architecture.check(self.root))

    def test_diagnostic_contract_and_collector_are_rejected(self):
        self.write('src/Playground.Contracts/Warehouse.cs', 'public record Response(string TraceId);')
        self.write('infra/compose.yaml', 'services:\n  collector:\n    image: otel/opentelemetry-collector:1.0\n')
        self.assertEqual(2, len(architecture.check(self.root)))


if __name__ == '__main__':
    unittest.main()
