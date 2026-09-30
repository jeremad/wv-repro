var builder = DistributedApplication.CreateBuilder(args);

var database = builder.AddPostgres("postgres").WithImageTag("18.3").AddDatabase("wolverine");

builder.AddProject<Projects.ReproApp>("repro").WithReference(database).WaitFor(database);

builder.Build().Run();
