using AnalizadorSql.Client.Pages;
using AnalizadorSql.Components;

var builder = WebApplication.CreateBuilder(args);

// Registrar HttpClient en el servidor
builder.Services.AddHttpClient();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(AnalizadorSql.Client._Imports).Assembly);

app.MapPost("/api/analizar", (SolicitudSql solicitud) =>
{
    var analizador = new AnalizadorSql.src.AnalizadorSql();

    var resultado = analizador.Analizar(solicitud.Codigo ?? "");

    return Results.Ok(resultado);
});

app.Run();

public record SolicitudSql(string Codigo);