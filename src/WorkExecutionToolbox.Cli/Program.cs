using WorkExecutionToolbox;
using WorkExecutionToolbox.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
var provider = new GitHubIssueProvider(http, GhAuthentication.ReadTokenAsync);
return await ToolboxCommand.RunAsync(args, provider, provider, Console.Out, Console.Error, cancellation.Token);
