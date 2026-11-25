using System.Net;
using System.Security.Claims;
using foundry_chatbot_api.Configuration;
using Microsoft.AspNetCore.Http;

namespace foundry_chatbot_api.Tests;

public class ChatRateLimitPartitionTests
{
    [Fact]
    public void AuthenticatedUsersBehindTheSameProxyHaveSeparateLimits()
    {
        var first = CreateContext("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var second = CreateContext("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        Assert.NotEqual(ChatRateLimitPartition.GetKey(first), ChatRateLimitPartition.GetKey(second));
        Assert.Equal(ChatRateLimitPartition.GetKey(first), ChatRateLimitPartition.GetKey(CreateContext("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")));
    }

    [Fact]
    public void UnauthenticatedClaimsDoNotChooseThePartition()
    {
        var context = CreateContext("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", authenticated: false);
        Assert.Equal("ip:192.0.2.1", ChatRateLimitPartition.GetKey(context));
    }

    private static DefaultHttpContext CreateContext(string objectId, bool authenticated = true)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("tid", "cccccccc-cccc-cccc-cccc-cccccccccccc"), new Claim("oid", objectId)],
                authenticated ? "test" : null))
        };
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
        return context;
    }
}
