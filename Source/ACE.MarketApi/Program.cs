using System;

namespace ACE.MarketApi
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                // the build's OpenAPI step: write the document and exit, without the database or the DATs
                if (args.Length == 2 && args[0] == "--generate-openapi")
                {
                    MarketOpenApi.WriteAsync(args[1]).GetAwaiter().GetResult();
                    return 0;
                }

                MarketApi.Create(args).Run();
                return 0;
            }
            catch (MarketUnavailableException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }
    }
}
