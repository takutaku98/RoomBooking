var builder = DistributedApplication.CreateBuilder(args);

// データはボリュームに残さない。起動のたびにまっさらな DB から始める（ADR-0003）
var postgres = builder.AddPostgres("postgres")
    .WithImageTag("18");

var database = postgres.AddDatabase("roombooking");

builder.AddProject<Projects.RoomBooking_Api>("api")
    .WithReference(database)
    .WaitFor(database)
    .WithHttpHealthCheck("/health");

builder.Build().Run();
