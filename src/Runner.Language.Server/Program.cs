﻿using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Newtonsoft.Json.Linq;

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Window;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using OmniSharp.Extensions.LanguageServer.Server;

using Runner.Language.Server;

#if WASM
Console.WriteLine("Loading Lsp...");

_ = Task.Run(async () => {
    var crlf = System.Text.Encoding.UTF8.GetBytes("\r\n\r\n");
    while(true) {
        Console.WriteLine("Read Message");
        var result = await Interop.Output.Reader.ReadAsync();
        Console.WriteLine("Got Message len = " + result.Buffer.Length);
        var header = "";
        var body = "";
        var remainingBody = 0;
        foreach (var cp in result.Buffer)
        {
            var span = cp;
            while(span.Length > 0) {
                if(remainingBody == 0) {
                    int endOfHeader = span.Span.IndexOf(crlf);
                    if(endOfHeader == -1) {
                        header += System.Text.Encoding.UTF8.GetString(span.Span);

                        span = span.Slice(span.Length);
                        // Console.WriteLine(header);
                    } else {
                        header += System.Text.Encoding.UTF8.GetString(span.Slice(0, endOfHeader).Span);
                        
                        Console.WriteLine(header);

                        remainingBody = int.Parse(header.Split(": ")[1]);

                        header = "";
                        body = "";
                        span = span.Slice(endOfHeader + 4);
                    }
                } else {
                    var n = Math.Min(span.Length, remainingBody);
                    body += System.Text.Encoding.UTF8.GetString(span.Slice(0, n).Span);
                    remainingBody -= n;
                    span = span.Slice(n);
                    if(remainingBody == 0) {
                        await Interop.SendOutputMessageAsync(body);
                    }
                }
            }
        }
        Interop.Output.Reader.AdvanceTo(result.Buffer.End);
        // await Interop.SendOutputMessageAsync(result.Buffer.ToArray());
    }
}).ConfigureAwait(false);
#endif

var server = await LanguageServer.From(
    options =>
        options
#if WASM
            .WithInput(Interop.Input.Reader)
            .WithOutput(Interop.Output.Writer)
#else
            .WithInput(Console.OpenStandardInput())
            .WithOutput(Console.OpenStandardOutput())
#endif
            .ConfigureLogging(
                x => x
                    .AddLanguageProtocolLogging()
                    .SetMinimumLevel(LogLevel.Debug)
            )
           .WithHandler<AutoCompleter>()
           .WithHandler<TextDocumentSyncHelper>()
           .WithHandler<WorkspaceFolderListener>()
           .WithHandler<HoverProvider>()
           .WithHandler<SemanticTokenHandler>()
           .WithHandler<CodeLensProvider>()
            .WithServices(x => x.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace)))
            .WithServices(
                services =>
                {
                    services.AddSingleton(new SharedData());
                    services.AddSingleton(
                        new ConfigurationItem
                        {
                            Section = "yaml",
                        }
                    ).AddSingleton(
                        new ConfigurationItem
                        {
                            Section = "azure-pipelines",
                        }
                    );
                }
            )
            .OnInitialize(
                (server, request, token) =>
                {
                    return Task.CompletedTask;
                }
            )
            .OnInitialized(
                (server, request, response, token) =>
                {
                    var data = server.GetRequiredService<SharedData>();
                    if(request.WorkspaceFolders?.Any() == true) {
                        data.RootFolders.AddRange(request.WorkspaceFolders);
                    }
                    return Task.CompletedTask;
                }
            )
            .OnStarted(
                async (languageServer, token) =>
                {
                    var data = languageServer.GetRequiredService<SharedData>();
                    data.Server = languageServer;
                    var logger = languageServer.Services.GetRequiredService<ILogger<Program>>();
                    var configuration = await languageServer.Configuration.GetConfiguration(
                        new ConfigurationItem
                        {
                            Section = "yaml",
                        }, new ConfigurationItem
                        {
                            Section = "azure-pipelines",
                        }
                    ).ConfigureAwait(false);

                    var baseConfig = new JObject();
                    foreach (var config in languageServer.Configuration.AsEnumerable())
                    {
                        baseConfig.Add(config.Key, config.Value);
                    }

                    logger.LogInformation("Base Config: {@Config}", baseConfig);

                    var scopedConfig = new JObject();
                    foreach (var config in configuration.AsEnumerable())
                    {
                        scopedConfig.Add(config.Key, config.Value);
                    }

                    logger.LogInformation("Scoped Config: {@Config}", scopedConfig);
                }
            )
).ConfigureAwait(false);

await server.WaitForExit.ConfigureAwait(false);

#if WASM

public static partial class Interop {
    [JSImport("sendOutputMessageAsync", "extension.js")]
    internal static partial Task SendOutputMessageAsync(string message);

    [SupportedOSPlatform("browser")]
    [JSExport]
    public async static Task SendInputMessageAsync(string message) {
        Console.WriteLine("Message received");
        Console.WriteLine(message);

        var rawMsg = System.Text.Encoding.UTF8.GetBytes(message);
        var header = System.Text.Encoding.UTF8.GetBytes("Content-Length: " + rawMsg.Length + "\r\n\r\n");

        var fr = await Input.Writer.WriteAsync(header);
        await Input.Writer.WriteAsync(rawMsg);
    }

    public static Pipe Input = new Pipe();
    public static Pipe Output = new Pipe();
}
#endif
