import { commands, window, ExtensionContext, Uri, ViewColumn, env, workspace, languages, TreeItem, TextEditor, WebviewPanel, QuickPickItem } from 'vscode';
import { LanguageClient, TransportKind } from 'vscode-languageclient/node';
import { join } from 'path';
import { ChildProcessWithoutNullStreams, spawn } from 'child_process';
import { RSTreeDataProvider } from './treeItemProvider';
import {LogScheme} from "./logs/constants";
import {WorkflowStepLogProvider} from "./logs/fileProvider";
import {WorkflowStepLogFoldingProvider} from "./logs/foldingProvider";
import {WorkflowStepLogSymbolProvider} from "./logs/symbolProvider";
import { getLogInfo } from './logs/logInfo';
import { updateDecorations } from './logs/formatProvider';
import { buildLogURI } from './logs/scheme';
import { createSocket } from 'dgram';

var startRunner : ChildProcessWithoutNullStreams | null = null;
var finishPromise : Promise<void> | null = null;

function getExternalIP() {
	return new Promise((resolve, reject) => {
		const socket = createSocket('udp4');
		socket.connect(65530, '8.8.8.8', () => {
			const address = socket.address();
			resolve(address.address);
		});
	}).catch(() => {
		return "localhost";
	});
}

function activate(context : ExtensionContext) {
	// Log providers

	context.subscriptions.push(
		languages.registerFoldingRangeProvider({scheme: LogScheme}, new WorkflowStepLogFoldingProvider())
	);

	context.subscriptions.push(
		languages.registerDocumentSymbolProvider(
		{
			scheme: LogScheme
		},
		new WorkflowStepLogSymbolProvider()
		)
	);

	context.subscriptions.push(
		window.onDidChangeActiveTextEditor((editor : TextEditor) => {
		if (editor.document.uri?.scheme === LogScheme) {
			const logInfo = getLogInfo(editor.document.uri);
			if (logInfo) {
				updateDecorations(editor, logInfo);
			}
		}
	}));

	context.subscriptions.push(
		commands.registerCommand("runner.server.workflow.logs", async (obj : TreeItem) => {
			var jobId = obj.command.arguments[1]; //await window.showInputBox({ prompt: "JobId", ignoreFocusOut: true, title: "Provide jobid" })
			const uri = buildLogURI(
			`${obj.label}`,
			"Unknown",
			"Unknown",
			jobId
			);

			const doc = await workspace.openTextDocument(uri);
			const editor = await window.showTextDocument(doc, {
			preview: false
			});

			const logInfo = getLogInfo(uri);
			if (!logInfo) {
			throw new Error("Could not get log info");
			}

			// Custom formatting after the editor has been opened
			updateDecorations(editor, logInfo);
		})
	);

	(async() => {
		console.log("Aquire Dotnet!")

		// App requires .NET 8.0
		const commandRes = await commands.executeCommand('dotnet.acquire', { version: '8.0', requestingExtensionId: `${context.extension.packageJSON.publisher}.${context.extension.packageJSON.name}`, mode: "aspnetcore" }) as any;
		const dotnetPath = commandRes.dotnetPath;
		console.log("Dotnet " + dotnetPath)

		if (!dotnetPath) {
			throw new Error('Could not resolve the dotnet path!');
		}

		console.log("Starting Language Server")
		var client = new LanguageClient(
			'Runner.Server',
			'Runner.Server Language Server',
			{
				run: {
					transport: TransportKind.stdio,
					command: dotnetPath,
					args: [ join(context.extensionPath, 'native', 'Runner.Language.Server.dll') ]
				},
				debug: {
					transport: TransportKind.stdio,
					command: dotnetPath,
					args: [ join(context.extensionPath, 'native', 'Runner.Language.Server.dll') ]
				}
			},
			{
				documentSelector: [ { language: "yaml" }, { language: "azure-pipelines" } ],
			}
		);

		var stopServer = new AbortController();

		var serverproc = spawn(dotnetPath, [ join(context.extensionPath, 'native', 'Runner.Server.dll'), '--urls', 'http://*:0' ], { killSignal: 'SIGINT', signal: stopServer.signal, windowsHide: true, stdio: 'pipe', shell: false });
		context.subscriptions.push({ dispose: () => stopServer.abort() })
		serverproc.addListener('exit', code => {
			console.log(code);
		});
		var address : string | null = null;
		serverproc.stdout.on('data', async (data) => {
			var sdata = data.asciiSlice();
			console.log(sdata)
			var i = sdata.indexOf("http://");
			if(i !== -1) {
				var end = sdata.indexOf('\n', i + 1);
				var externalAddress = await getExternalIP();
				address = sdata.substring(i, end).replace("[::]", externalAddress).replace("0.0.0.0", externalAddress).trim();
				var webviewAddress = sdata.substring(i, end).replace("[::]", "localhost").replace("0.0.0.0", "localhost").trim();

				window.registerTreeDataProvider("workflow-view", new RSTreeDataProvider(context, address));

				context.subscriptions.push(
					workspace.registerTextDocumentContentProvider(LogScheme, new WorkflowStepLogProvider(address))
				);

				if(address) {
					var args = [ join(context.extensionPath, 'native', 'Runner.Client.dll'), 'startrunner', '--parallel', '4' ];
					args.push('--server', address)
				}

				finishPromise = new Promise<void>(onexit => {
					startRunner = spawn(dotnetPath, args, { windowsHide: true, stdio: 'pipe', shell: false, env: { ...process.env, RUNNER_CLIENT_EXIT_AFTER_ENTER: "1" } });
					startRunner.stdout.on('data', async (data) => {
						var sdata = data.asciiSlice();
						console.log(sdata)
					});
					startRunner.addListener('exit', code => {
						console.log(code);
						onexit();
					});
				})

				var panel: WebviewPanel;
				var getPanel = async () => {
					if(panel) {
						return panel;
					}
					panel = window.createWebviewPanel(
						"runner.server",
						"Runner Server",
						ViewColumn.One,
						{
							enableScripts: true,
							// Without this we loose your webview position when the webview is in background
							retainContextWhenHidden: true
						}
					);
					context.subscriptions.push(panel);

					panel.onDidDispose(() => {
						panel = null;
					})
					return panel;
				}

				const fullWebServerUri = webviewAddress && await env.asExternalUri(
					Uri.parse(webviewAddress)
				);

				var openPanel = async(name: string, url : string) => {
					await getPanel();
					panel.title = name;
					const cspSource = panel.webview.cspSource;
					// Get the content Uri
					const style = panel.webview.asWebviewUri(
						Uri.joinPath(context.extensionUri, 'style.css')
					);
					panel.webview.html = `<!DOCTYPE html>
					<html>
						<head>
							<meta
								http-equiv="Content-Security-Policy"
								content="default-src 'none'; frame-src ${fullWebServerUri} ${cspSource} https:; img-src ${cspSource} https:; script-src ${cspSource}; style-src ${cspSource};"
							/>
							<meta name="viewport" content="width=device-width, initial-scale=1.0">
							<link rel="stylesheet" href="${style}">
						</head>
						<body>
							<iframe src="${fullWebServerUri}${url}"></iframe>
						</body>
					</html>`;
					panel.reveal(ViewColumn.One, true);
				};

				commands.registerCommand("runner.server.openjob", (runId, id, name) => {
					openPanel(name, `?view=allworkflows&extension=1#/0/${runId}/0/${id}`);
				});

				commands.registerCommand("runner.server.openjobexternal", (obj : TreeItem) => {
					env.openExternal(Uri.parse(`${fullWebServerUri}?view=allworkflows#/0/${obj.command.arguments[0]}/0/${obj.command.arguments[1]}`));
				});

				commands.registerCommand("runner.server.openworkflowrun", runId => {
					openPanel(`#${runId}`, `?view=allworkflows&extension=1#/0/${runId}/0`);
				});

				commands.registerCommand("runner.server.openworkflowrunexternal", (obj : TreeItem) => {
					env.openExternal(Uri.parse(`${fullWebServerUri}?view=allworkflows#/0/${obj.command.arguments[0]}/0`));
				});
			}
		});
		serverproc.stderr.on('data', async (data) => {
			var sdata = data.asciiSlice();
			console.log(sdata)
		});
		
		commands.registerCommand("runner.server.start-client", () => {
			var args = [ join(context.extensionPath, 'native', 'Runner.Client.dll'), '--interactive' ];
			if(address) {
				args.push('--server', address)
			}
			context.subscriptions.push(window.createTerminal("runner.client", dotnetPath, args))
		});

		commands.registerCommand("runner.server.runworkflow", async (workflow, events) => {
			console.log(`runner.server.runjob {workflow}`)
			var sel : string = events.length === 1 ? events : await window.showQuickPick(events, { canPickMany: false })
			var args = [ join(context.extensionPath, 'native', 'Runner.Client.dll'), '--event', sel || 'push', '-W', Uri.parse(workflow).fsPath ];
			if(address) {
				args.push('--server', address)
			}

			let startproc = spawn(dotnetPath, args, { windowsHide: true, stdio: 'pipe', shell: false, env: { ...process.env }, cwd: workspace.getWorkspaceFolder(Uri.parse(workflow))?.uri?.fsPath });
			startproc.stdout.on('data', async (data) => {
				var sdata = data.asciiSlice();
				console.log(sdata)
			});
			startproc.stderr.on('data', async (data) => {
				var sdata = data.asciiSlice();
				console.log(sdata)
			});
			startproc.addListener('exit', code => {
				console.log(code);
			});
		});
		commands.registerCommand("runner.server.runjob", async (workflow, job, events) => {
			if(typeof job === 'object') {
				var jobs : QuickPickItem[] = [];
				for(var j of job) {
					jobs.push({
						label: j.name,
						detail: j.jobIdLong
					});
				}
				job = (await window.showQuickPick(jobs, { canPickMany: false, title: "Select matrix job entry" }))?.detail;
				if(!job) {
					throw new Error("No job selected");
				}
			}
			console.log(`runner.server.runjob {workflow}.{job}`)
			var sel : string = events.length === 1 ? events : await window.showQuickPick(events, { canPickMany: false })
			var args = [ join(context.extensionPath, 'native', 'Runner.Client.dll'), '--event', sel || 'push', '-W', Uri.parse(workflow).fsPath, '-j', job ];
			if(address) {
				args.push('--server', address)
			}
			
			let startproc = spawn(dotnetPath, args, { windowsHide: true, stdio: 'pipe', shell: false, env: { ...process.env }, cwd: workspace.getWorkspaceFolder(Uri.parse(workflow))?.uri?.fsPath });
			startproc.stdout.on('data', async (data) => {
				var sdata = data.asciiSlice();
				console.log(sdata)
			});
			startproc.stderr.on('data', async (data) => {
				var sdata = data.asciiSlice();
				console.log(sdata)
			});
			startproc.addListener('exit', code => {
				console.log(code);
			});
		});

		context.subscriptions.push(commands.registerCommand("runner.server.workflow.cancel", async (obj : TreeItem) => {
			var items : QuickPickItem[] = [];
			if(obj.contextValue?.indexOf("job") !== -1) {
				items.push({
					label: "Cancel",
					description: "Cancel only this job"
				});
				items.push({
					label: "Force Cancel",
					description: "Force Cancel only this job"
				});
			} else {
				items.push({
					label: "Cancel",
					description: "Cancel this workflow run"
				});
				items.push({
					label: "Force Cancel",
					description: "Force Cancel this workflow run"
				});
			}
			var selection = await window.showQuickPick(items);
			if(!selection) {
				return;
			}
			if(obj.contextValue?.indexOf("job") !== -1) {
				await fetch(address + "/_apis/v1/Message/cancel/" + obj.command.arguments[1] + "?force=" + (selection.label.indexOf("Force") !== -1), { method: "POST" });
			} else {
				await fetch(address + "/_apis/v1/Message/" + (selection.label.indexOf("Force") !== -1 ? "forceCancelWorkflow" : "cancelWorkflow") + "/" + obj.command.arguments[0], { method: "POST" });
			}
		}));

		context.subscriptions.push(client);
		client.start();
	})();
}

// this method is called when your extension is deactivated
async function deactivate() {
	if(finishPromise !== null && startRunner !== null) {
		startRunner.stdin.write("\n");
		await finishPromise;
	}
}
module.exports = {
	activate,
	deactivate
}
