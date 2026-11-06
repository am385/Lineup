using Lineup.Integration.Streaming.Sweep.FaultSource;

var app = FaultSourceApplication.Build(args);
await app.RunAsync();
