var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.RoomBooking_Api>("api")
    .WithHttpHealthCheck("/health");

builder.Build().Run();
