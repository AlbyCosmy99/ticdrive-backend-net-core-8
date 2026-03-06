using Autofac;
using Autofac.Extensions.DependencyInjection;
using TicDrive.AppConfig;
using TicDrive.Context;
using Microsoft.EntityFrameworkCore;
using TicDrive.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.OpenApi.Models;
using TicDrive.Utils.Auth;
using Azure.Storage.Blobs;
using Npgsql;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "TicDrive API", Version = "v1" });

    var jwtSecurityScheme = new OpenApiSecurityScheme
    {
        Scheme = "bearer",
        BearerFormat = "JWT",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Description = "Inserisci il token JWT",
        Reference = new OpenApiReference
        {
            Id = "Bearer",
            Type = ReferenceType.SecurityScheme
        }
    };

    c.AddSecurityDefinition("Bearer", jwtSecurityScheme);
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { jwtSecurityScheme, Array.Empty<string>() }
    });
});

builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);

builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory())
.ConfigureContainer<ContainerBuilder>(autofacBuilder =>
{
    autofacBuilder.RegisterModule(new AutofacModule());
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy => policy
        .AllowAnyOrigin()
        .AllowAnyMethod()
        .AllowAnyHeader());
});

builder.Services.AddIdentity<User, IdentityRole>(options =>
{
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequiredLength = 8;
    options.Password.RequiredUniqueChars = 1;
})
.AddEntityFrameworkStores<TicDriveDbContext>()
.AddDefaultTokenProviders();

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
}).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["JwtSettings:Issuer"],
        ValidAudience = builder.Configuration["JwtSettings:Audience"],
        IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
            System.Text.Encoding.UTF8.GetBytes(builder.Configuration["JwtSettings:SecretKey"]))
    };
});

builder.Services.AddScoped<IClaimsTransformation, UserClaimsMapper>();
builder.Services.AddScoped<LoginLogger>();

var connection = ResolvePostgresConnectionString(builder.Configuration, builder.Environment);

builder.Services.AddDbContext<TicDriveDbContext>(options =>
    options.UseNpgsql(connection)
);

builder.Services.AddAutoMapper(typeof(AutomapperConfig));

builder.Services.AddSingleton(x =>
{
    var config = x.GetRequiredService<IConfiguration>();
    var connString = config["Azure:BlobStorageConnectionString"];
    return new BlobServiceClient(connString);
});

builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "global",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 1000,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }));

    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = 429;
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsync(
            "{\"error\": \"Too many requests. Please wait and try again.\"}", token);
    };
});



var app = builder.Build();

app.UseRateLimiter();
app.MapControllers();

app.UseCors("AllowAll");

app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.Run();

static string ResolvePostgresConnectionString(ConfigurationManager configuration, IWebHostEnvironment environment)
{
    var preferredVariableNames = environment.IsDevelopment()
        ? new[]
        {
            "TICDRIVE_DEVELOPMENT_POSTGRESQL_CONNECTIONSTRING",
            "TICDRIVE_DEVELOPMENT_DATABASE_URL"
        }
        : new[]
        {
            "TICDRIVE_PRODUCTION_POSTGRESQL_CONNECTIONSTRING",
            "TICDRIVE_PRODUCTION_DATABASE_URL"
        };

    var fallbackVariableNames = new[]
    {
        "TICDRIVE_POSTGRESQL_CONNECTIONSTRING",
        "TICDRIVE_DATABASE_URL",
        "TICDRIVE_RAILWAY_POSTGRESQL_CONNECTIONSTRING",
        "DATABASE_URL",
        "DATABASE_PUBLIC_URL"
    };

    var rawConnectionString = preferredVariableNames
        .Concat(fallbackVariableNames)
        .Select(Environment.GetEnvironmentVariable)
        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
        ?? configuration.GetConnectionString(environment.IsDevelopment()
            ? "TICDRIVE_DEVELOPMENT_POSTGRESQL_CONNECTIONSTRING"
            : "TICDRIVE_PRODUCTION_POSTGRESQL_CONNECTIONSTRING")
        ?? configuration.GetConnectionString("TICDRIVE_RAILWAY_POSTGRESQL_CONNECTIONSTRING")
        ?? throw new InvalidOperationException(
            "Missing PostgreSQL connection string. Configure a Railway DB env var for the active environment.");

    return NormalizePostgresConnectionString(rawConnectionString);
}

static string NormalizePostgresConnectionString(string rawConnectionString)
{
    if (!Uri.TryCreate(rawConnectionString, UriKind.Absolute, out var uri) ||
        (uri.Scheme != "postgres" && uri.Scheme != "postgresql"))
    {
        return rawConnectionString;
    }

    var userInfoParts = uri.UserInfo.Split(':', 2);

    var builder = new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.IsDefaultPort ? 5432 : uri.Port,
        Username = userInfoParts.Length > 0 ? Uri.UnescapeDataString(userInfoParts[0]) : string.Empty,
        Password = userInfoParts.Length > 1 ? Uri.UnescapeDataString(userInfoParts[1]) : string.Empty,
        Database = uri.AbsolutePath.Trim('/'),
        SslMode = SslMode.Require,
        TrustServerCertificate = true
    };

    foreach (var segment in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
        var pair = segment.Split('=', 2);
        var key = Uri.UnescapeDataString(pair[0]).ToLowerInvariant();
        var value = pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty;

        switch (key)
        {
            case "sslmode":
                if (Enum.TryParse<SslMode>(value, true, out var sslMode))
                {
                    builder.SslMode = sslMode;
                }
                break;
            case "trustservercertificate":
            case "trust_server_certificate":
                if (bool.TryParse(value, out var trustServerCertificate))
                {
                    builder.TrustServerCertificate = trustServerCertificate;
                }
                break;
        }
    }

    return builder.ConnectionString;
}
