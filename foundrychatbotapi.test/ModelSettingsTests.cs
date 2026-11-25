using System.Security.Claims;
using foundry_chatbot_api.Configuration;
using foundry_chatbot_api.Controllers;
using foundry_chatbot_api.Models;
using foundry_chatbot_api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace foundry_chatbot_api.Tests;

public class ModelSettingsTests
{
    [Fact]
    public void AdminAuthorizationRequiresAllowlistedObjectIdAndChatPolicy()
    {
        const string id = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", id)], "test"));
        Assert.True(AdminAccess.IsAllowed(user, [id.ToUpperInvariant()]));
        Assert.False(AdminAccess.IsAllowed(user, []));
        Assert.False(AdminAccess.IsAllowed(user, ["invalid"]));
        Assert.False(AdminAccess.IsAllowed(new ClaimsPrincipal(), [id]));
        var policies = typeof(ModelSettingsController).GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>().Select(attribute => attribute.Policy).ToArray();
        Assert.Contains("ChatAccess", policies);
        Assert.Contains("AdminAccess", policies);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(2.0)]
    public async Task SettingsAreSavedAndRead(double? temperature)
    {
        var store = new Store();
        var controller = new ModelSettingsController(store, NullLogger<ModelSettingsController>.Instance);
        var saved = Assert.IsType<OkObjectResult>(await controller.Put(
            new UpdateModelSettingsRequest { Temperature = temperature }, CancellationToken.None));
        Assert.Equal(temperature, Assert.IsType<ModelSettingsResponse>(saved.Value).Temperature);
        var read = Assert.IsType<OkObjectResult>(await controller.Get(CancellationToken.None));
        Assert.Equal(temperature, Assert.IsType<ModelSettingsResponse>(read.Value).Temperature);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(2.1)]
    [InlineData(double.NaN)]
    public async Task InvalidTemperatureIsRejected(double temperature)
    {
        var store = new Store();
        var controller = new ModelSettingsController(store, NullLogger<ModelSettingsController>.Instance);
        Assert.IsType<BadRequestObjectResult>(await controller.Put(
            new UpdateModelSettingsRequest { Temperature = temperature }, CancellationToken.None));
        Assert.Null(store.Temperature);
    }

    private sealed class Store : IModelSettingsStore
    {
        public double? Temperature { get; private set; }
        public Task<double?> GetTemperatureAsync(CancellationToken cancellationToken) => Task.FromResult(Temperature);
        public Task SetTemperatureAsync(double? temperature, CancellationToken cancellationToken)
        {
            Temperature = temperature;
            return Task.CompletedTask;
        }
    }
}
