using HidControl.ClientSdk;

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// Skeleton entrypoint. Replace with Blazor once we have time.
app.MapGet("/", () => "HidControl.Web skeleton (client sdk wired).");

// Touch the SDK so the project reference is exercised.
app.MapGet("/sdk/ping", () =>
{
    _ = new HidControlClient(new HttpClient(), new Uri("http://127.0.0.1:8080"));
    return Results.Ok(new { ok = true });
});

app.Run();
