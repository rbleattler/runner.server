using System;

using GitHub.DistributedTask.Expressions2;
using GitHub.DistributedTask.ObjectTemplating;
using GitHub.DistributedTask.Pipelines.ObjectTemplating;
using GitHub.DistributedTask.ObjectTemplating.Schema;
using GitHub.DistributedTask.ObjectTemplating.Tokens;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using Runner.Server.Azure.Devops;

using Sdk.Actions;

namespace Runner.Language.Server;

public class HoverProvider : IHoverHandler
{
    private SharedData data;

    public HoverProvider(SharedData data) {
        this.data = data;
    }


    public HoverRegistrationOptions GetRegistrationOptions(HoverCapability capability, ClientCapabilities clientCapabilities)
    {
        return new HoverRegistrationOptions { DocumentSelector = new TextDocumentSelector(new TextDocumentFilter() { Language = "yaml" }, new TextDocumentFilter() { Language = "azure-pipelines" }) };
    }

    private static TemplateContext CreateTemplateContext(GitHub.DistributedTask.ObjectTemplating.ITraceWriter traceWriter) {
        ExpressionFlags flags = ExpressionFlags.None;

        var templateContext = new TemplateContext() {
            Flags = flags,
            CancellationToken = CancellationToken.None,
            Errors = new TemplateValidationErrors(10, 500),
            Memory = new TemplateMemory(
                maxDepth: 100,
                maxEvents: 1000000,
                maxBytes: 10 * 1024 * 1024),
            TraceWriter = traceWriter,
            Schema = PipelineTemplateSchemaFactory.GetSchema()
        };
        return templateContext;
    }


    public async Task<Hover?> Handle(HoverParams request, CancellationToken cancellationToken)
    {
        string content;
        if(!data.Content.TryGetValue(request.TextDocument.Uri, out content)) {
            return null;
        }
        var currentFileName = "t.yml";
        var files = new Dictionary<string, string>() { { currentFileName, content } };

        var row = request.Position.Line + 1;
        var column = request.Position.Character + 1;
            
        var context = new Runner.Server.Azure.Devops.Context {
            FileProvider = new DefaultInMemoryFileProviderFileProvider(files.ToArray()),
            TraceWriter = new GitHub.DistributedTask.ObjectTemplating.EmptyTraceWriter(),
            Flags = GitHub.DistributedTask.Expressions2.ExpressionFlags.DTExpressionsV1 | GitHub.DistributedTask.Expressions2.ExpressionFlags.ExtendedDirectives | GitHub.DistributedTask.Expressions2.ExpressionFlags.AllowAnyForInsert,
            Column = column,
            Row = row,
            RawMapping = true
        };
        GitHub.DistributedTask.ObjectTemplating.Schema.TemplateSchema? schema = null;
        bool azurePipelines = false;
        try {
            var isWorkflow = request.TextDocument.Uri.Path.Contains("/.github/workflows/");
            if(isWorkflow || request.TextDocument.Uri.Path.Contains("/action.yml") || request.TextDocument.Uri.Path.Contains("/action.yaml")) {
                var templateContext = CreateTemplateContext(new EmptyTraceWriter());
                context.AutoCompleteMatches = new List<AutoCompleteEntry>();
                context.Flags = templateContext.Flags;
                templateContext.AutoCompleteMatches = context.AutoCompleteMatches;
                templateContext.Column = context.Column;
                templateContext.Row = context.Row;
                if(!isWorkflow) {
                    templateContext.Schema = PipelineTemplateSchemaFactory.GetActionSchema();
                }
                // Get the file ID
                var fileId = templateContext.GetFileId(currentFileName);

                // Read the file
                var fileContent = content;
                var yamlObjectReader = new YamlObjectReader(fileId, fileContent, rawMapping: true);
                TemplateReader.Read(templateContext, isWorkflow ? "workflow-root" : "action-root", yamlObjectReader, fileId, out _);
                schema = templateContext.Schema;
            } else {
                azurePipelines = true;
                schema = AzureDevops.LoadSchema();
                var template = await AzureDevops.ParseTemplate(context, currentFileName, this.data.Schema[request.TextDocument.Uri], true);
                _ = template;
            }
        } catch
        {
        }
        var last = context.AutoCompleteMatches?.LastOrDefault();
        if(last?.Tokens?.Any() == true) {
            var tkn = last.Tokens.LastOrDefault(t => t.Index <= last.Index);
            if(tkn == null || tkn.Kind == GitHub.DistributedTask.Expressions2.Tokens.TokenKind.String) {
                return null;
            }

            var i = last.Tokens.IndexOf(tkn);

            var desc = azurePipelines ? PipelinesDescriptions.LoadDescriptions() : ActionsDescriptions.LoadDescriptions();
            
            return new Hover { Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(new OmniSharp.Extensions.LanguageServer.Protocol.Models.Position(row - 1, column - 1 - (last.Index - tkn.Index)), new OmniSharp.Extensions.LanguageServer.Protocol.Models.Position(row - 1, column - 1 - (last.Index - tkn.Index) + tkn.RawValue.Length)),
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent() { Kind = MarkupKind.Markdown, Value = 
                    i > 2 && last.Tokens[i - 2].Kind == GitHub.DistributedTask.Expressions2.Tokens.TokenKind.NamedValue && last.Tokens[i - 1].Kind == GitHub.DistributedTask.Expressions2.Tokens.TokenKind.Dereference && new [] { "github", "runner", "strategy" }.Contains(last.Tokens[i - 2].RawValue.ToLower()) && desc[last.Tokens[i - 2].RawValue].TryGetValue(tkn.RawValue, out var d)
                    || i > 4 && last.Tokens[i - 4].Kind == GitHub.DistributedTask.Expressions2.Tokens.TokenKind.NamedValue && last.Tokens[i - 3].Kind == GitHub.DistributedTask.Expressions2.Tokens.TokenKind.Dereference 
                    && last.Tokens[i - 2].Kind == GitHub.DistributedTask.Expressions2.Tokens.TokenKind.PropertyName && last.Tokens[i - 1].Kind == GitHub.DistributedTask.Expressions2.Tokens.TokenKind.Dereference && new [] { "steps", "jobs", "needs" }.Contains(last.Tokens[i - 4].RawValue.ToLower()) && desc[last.Tokens[i - 4].RawValue].TryGetValue(tkn.RawValue, out d)
                    || desc["root"].TryGetValue(tkn.RawValue, out d)
                    || desc["functions"].TryGetValue(tkn.RawValue, out d) ? d.Description : tkn.RawValue }) };
        }
        string descr = null;
        Definition[] mappings = last.Definitions.FirstOrDefault() is OneOfDefinition oneOf ? oneOf.OneOf.Select(s => schema.GetDefinition(s)).ToArray() : last.Definitions.FirstOrDefault() is MappingDefinition xmdef ? new [] { xmdef } : null;
        if(mappings != null && last.Token is ScalarToken) {
            foreach(var def in mappings) {
                if(def is MappingDefinition mdef) {
                    descr ??= mdef.Properties.FirstOrDefault(p => p.Key.StartsWith(last.Token.ToString())).Value?.Description;
                }
            }
        }
        return last == null || last.Token.Line == null || last.Token.Column == null ? null : new Hover { Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(last.Token.PreWhiteSpace != null ? new OmniSharp.Extensions.LanguageServer.Protocol.Models.Position((int)last.Token.PreWhiteSpace.Line - 1, (int)last.Token.PreWhiteSpace.Character - 1) : new OmniSharp.Extensions.LanguageServer.Protocol.Models.Position(last.Token.Line.Value - 1, last.Token.Column.Value - 1), new OmniSharp.Extensions.LanguageServer.Protocol.Models.Position((int)last.Token.PostWhiteSpace.Line - 1, (int)last.Token.PostWhiteSpace.Character - 1)),
        Contents = new MarkedStringsOrMarkupContent(new MarkupContent() { Kind = MarkupKind.Markdown, Value = descr ?? last.Description ?? last.Definitions.FirstOrDefault()?.Description ?? "???" }) };
    }
}
