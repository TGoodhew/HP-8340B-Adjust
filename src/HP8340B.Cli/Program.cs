using HP8340B.Cli.Commands;
using Spectre.Console.Cli;

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("hp8340b");
    config.UseStrictParsing();
    config.ValidateExamples();

    config.AddCommand<ProbeCommand>("probe")
          .WithDescription("Enumerate the configured bench and confirm each instrument responds.")
          .WithExample("probe", "--sim");

    config.AddBranch("setup", setup =>
    {
        setup.SetDescription("Bench setups and hook-up cards.");

        setup.AddCommand<SetupListCommand>("list")
             .WithDescription("List every bench setup.");

        setup.AddCommand<SetupShowCommand>("show")
             .WithDescription("Print the hook-up card for one setup.")
             .WithExample("setup", "show", "S4");
    });

    config.AddBranch<RouteSettings>("route", route =>
    {
        route.SetDescription("Routed legs through the 3499A: what each carries and how high.");

        route.AddCommand<RouteStatusCommand>("status")
             .WithDescription("List every routed leg with its slot, channels and frequency limit.");

        route.AddCommand<RouteCardCommand>("card")
             .WithDescription("Print the routed SR/SV hook-up card for a campaign.");

        route.AddCommand<RouteShowCommand>("show")
             .WithDescription("Show one leg in full, with its band coverage.");
    });

    config.AddCommand<CodesCommand>("codes")
          .WithDescription("Show the 8340B HP-IB code table and which codes still need verifying.");
});

return app.Run(args);
