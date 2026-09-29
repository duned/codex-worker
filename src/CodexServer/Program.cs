namespace CodexServer;

public static class Program
{
    public static async Task Main(string[] args)
    {
        await using var app = await ServerApplication.BuildAsync(args);
        await app.RunAsync();
    }
}
