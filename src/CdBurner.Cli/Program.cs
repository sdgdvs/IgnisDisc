namespace CdBurner.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        return await CliCommandRunner.RunAsync(args, Console.Out, Console.Error).ConfigureAwait(false);
    }
}
