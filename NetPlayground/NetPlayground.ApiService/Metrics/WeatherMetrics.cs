using System.Diagnostics.Metrics;

namespace NetPlayground.ApiService.Metrics
{
    public class WeatherMetrics
    {
        private readonly Meter _meter;
        private readonly Counter<long> _weatherRequestsCounter;
        private readonly Histogram<int> _temperatures;

        public WeatherMetrics()
        {
            _meter = new Meter("NetPlayground.WeatherApi");

            _weatherRequestsCounter = _meter.CreateCounter<long>(
                name: "weather_requests_total",
                unit: "requests",
                description: "The total number of weather requests");

            _temperatures = _meter.CreateHistogram<int>(
                name: "weather_temperature_celsius",
                unit:"°C",
                description: "The temperature in Celsius");
        }

        public void IncrementWeatherRequests() => _weatherRequestsCounter.Add(1);
        public void RecordTemperature(int temperature) => _temperatures.Record(temperature);
    }
}
