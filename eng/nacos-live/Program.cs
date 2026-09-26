namespace NacosLiveAcceptance;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            if (args.Length == 2
                && args[0] == "--weather-fixture"
                && int.TryParse(args[1], out var port)
                && port is > 0 and < 65536)
            {
                await WeatherFixture.RunAsync(port, shutdown.Token);
                return 0;
            }

            var options = AcceptanceOptions.Parse(args);
            return await AcceptanceRunner.RunAsync(options, shutdown.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }
}