using Algolia.Search.Clients;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PlataformaIncidencias.Data;
using PlataformaIncidencias.Models;

var builder = WebApplication.CreateBuilder(args);

// La configuración de Algolia también puede llegar por variables de entorno con el prefijo
// ALGOLIA_ (ALGOLIA_APP_ID, ALGOLIA_SEARCH_KEY, ALGOLIA_INDEX_NAME). El proveedor estándar
// sólo reconoce la forma Algolia__AppId, por lo que se registra un mapeo explícito.
// Sólo se incorporan las variables presentes, para no pisar los valores de appsettings.json.
var algoliaPorVariableDeEntorno = new[]
    {
        (Clave: $"{AlgoliaSettings.SectionName}:AppId", Variable: "ALGOLIA_APP_ID"),
        (Clave: $"{AlgoliaSettings.SectionName}:SearchApiKey", Variable: "ALGOLIA_SEARCH_KEY"),
        (Clave: $"{AlgoliaSettings.SectionName}:IndexName", Variable: "ALGOLIA_INDEX_NAME")
    }
    .Select(par => (par.Clave, Valor: Environment.GetEnvironmentVariable(par.Variable)))
    .Where(par => !string.IsNullOrWhiteSpace(par.Valor))
    .ToDictionary(par => par.Clave, par => par.Valor);

if (algoliaPorVariableDeEntorno.Count > 0)
{
    builder.Configuration.AddInMemoryCollection(algoliaPorVariableDeEntorno);
}

builder.Services.Configure<AlgoliaSettings>(builder.Configuration.GetSection(AlgoliaSettings.SectionName));

// Add services to the container.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddDefaultIdentity<IdentityUser>(options => options.SignIn.RequireConfirmedAccount = true)
    .AddEntityFrameworkStores<ApplicationDbContext>();
builder.Services.AddControllersWithViews();

// Caché distribuida con Redis para el listado general de incidencias.
// La cadena de conexión se resuelve, por orden de precedencia, desde:
//   1. Variable de entorno / clave de configuración "RedisConnection".
//   2. ConnectionStrings:Redis (appsettings.json).
//   3. localhost:6379 como valor por defecto.
// "abortConnect=false" evita que la aplicación falle al arrancar si Redis no está disponible.
var redisConnection = builder.Configuration["RedisConnection"]
    ?? builder.Configuration.GetConnectionString("Redis")
    ?? "localhost:6379,abortConnect=false";

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = redisConnection;
    options.InstanceName = builder.Configuration["RedisInstanceName"] ?? "PlataformaIncidencias:";
});

// Cliente de sólo consulta de Algolia. Se construye con la Search API Key (nunca con la Admin API Key)
// y sólo se registra si hay credenciales: SearchConfig lanza si AppId o ApiKey vienen vacíos.
var algolia = builder.Configuration.GetSection(AlgoliaSettings.SectionName).Get<AlgoliaSettings>() ?? new AlgoliaSettings();
if (algolia.IsConfigured)
{
    builder.Services.AddSingleton<SearchClient>(sp => new SearchClient(
        new SearchConfig(algolia.AppId, algolia.SearchApiKey),
        sp.GetRequiredService<ILoggerFactory>()));

    builder.Services.AddSingleton<ISearchClient>(sp => sp.GetRequiredService<SearchClient>());
}

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "areas",
    pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.MapRazorPages()
   .WithStaticAssets();

app.Run();
