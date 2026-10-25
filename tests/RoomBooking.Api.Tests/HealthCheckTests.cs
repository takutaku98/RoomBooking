namespace RoomBooking.Api.Tests;

public class HealthCheckTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Health_ReturnsOk_WhenAppHostStarts()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.RoomBooking_AppHost>(cancellationToken);

        await using var app = await appHost.BuildAsync(cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);
        await app.StartAsync(cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("api", cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);

        // Act
        using var httpClient = app.CreateHttpClient("api");
        using var response = await httpClient.GetAsync("/health", cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
