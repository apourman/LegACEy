using System;

namespace ACE.MarketApi
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            try
            {
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
