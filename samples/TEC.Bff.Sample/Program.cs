using TEC.Bff;
using TEC.Security.AspNetCore;
using TEC.Security.DependencyInjection;
using TEC.Security.OpenIdConnect;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTecSecurity(builder.Configuration, security => security
    .AddAspNetCore()
    .AddOpenIdConnectWebLogin("Web", o =>
    {
        o.Authority = builder.Configuration["Bff:Authority"]!;
        o.ClientId = "meu-bff";
        o.ClientSecretName = "meu-bff-client-secret";   // o valor vem do TEC.Vault (um ISecretReader registrado)
    })
    .AddTecBff(bff =>
    {
        bff.ApiBaseAddress = new Uri(builder.Configuration["Bff:ApiBaseAddress"]!);
        bff.SpaRootPath = Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
        bff.Csp.ReportOnly = true;   // comece só relatando; imponha depois de zerar as violações
    }));

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapTecBff();

app.Run();
