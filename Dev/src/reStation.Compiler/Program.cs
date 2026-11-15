// @module: reStation.Compiler.Program
// @purpose: reStation.Compiler CLI entry point (exit 0 success, 1 compile failure, 2 usage error).
// @depends-on: reStation.Compiler.RecompCompilerPipeline
// @exports: Program
// @owner: devv-compiler (T5)

namespace reStation.Compiler;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args.Any(a => a is "--help" or "-h" or "/?"))
        {
            Console.WriteLine(RecompCompilerPipeline.UsageText);
            return args.Length == 0 ? 2 : 0;
        }

        RecompCompilerOptions options;
        try
        {
            options = RecompCompilerPipeline.ParseArgs(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        try
        {
            RecompCompileResult result = new RecompCompilerPipeline(options).Run();
            (result.Success ? Console.Out : Console.Error).WriteLine(result.Message);
            return result.Success ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Compilation canceled.");
            return 1;
        }
    }
}
