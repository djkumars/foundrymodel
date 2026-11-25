using System.Threading.RateLimiting;
using foundry_chatbot_api.Configuration;
using foundry_chatbot_api.Models;
using foundry_chatbot_api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;

var builder = WebApplication.CreateBuilder(args);

var tenantId = builder.Configuration["ExternalId:TenantId"];
var authority = builder.Configuration["ExternalId:Authority"];
var audience = builder.Configuration["ExternalId:Audience"];
var authConfigured = Guid.TryParse(tenantId, out var tenantGuid) &&
    Guid.TryParse(audience, out _) &&
    Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri) &&
    authorityUri.Scheme == Uri.UriSchemeHttps &&
    authorityUri.Host.EndsWith(".ciamlogin.com", StringComparison.OrdinalIgnoreCase) &&
    authorityUri.AbsolutePath.Trim('/').Equals($"{tenantGuid:D}/v2.0", StringComparison.OrdinalIgnoreCase) &&
    string.IsNullOrEmpty(authorityUri.UserInfo) &&
    string.IsNullOrEmpty(authorityUri.Query) &&
    string.IsNullOrEmpty(authorityUri.Fragment);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    if (authConfigured)
    {
        options.Authority = authority;
        options.Audience = audience;
    }
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            if (!authConfigured) context.NoResult();
            return Task.CompletedTask;
        }
    };
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ChatAccess", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
            authConfigured &&
            Guid.TryParse(context.User.FindFirst("tid")?.Value, out var tid) && tid == tenantGuid &&
            Guid.TryParse(context.User.FindFirst("oid")?.Value, out _) &&
            (context.User.FindFirst("scp")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains("access_as_user", StringComparer.Ordinal) ?? false));
    });
    options.AddPolicy("AdminAccess", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context => AdminAccess.IsAllowed(
            context.User, builder.Configuration.GetSection("Admin:AllowedObjectIds").Get<string[]>() ?? []));
    });
});

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.Configure<FoundryOptions>(builder.Configuration.GetSection(FoundryOptions.SectionName));
builder.Services.Configure<CosmosDbOptions>(builder.Configuration.GetSection(CosmosDbOptions.SectionName));
builder.Services.Configure<PromptShieldOptions>(builder.Configuration.GetSection(PromptShieldOptions.SectionName));
builder.Services.AddHttpClient<IFoundryChatService, FoundryChatService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient<IPromptShieldService, PromptShieldService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<IConversationStore, CosmosConversationStore>();
builder.Services.AddSingleton<IModelSettingsStore, CosmosModelSettingsStore>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new ErrorResponse("Too many chat requests. Please wait a moment and try again."),
            cancellationToken);
    };
    options.AddPolicy("chat", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            ChatRateLimitPartition.GetKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 30,
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(1)
            }));
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32 * 1024);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapControllers();
app.Run();

public partial class Program;
