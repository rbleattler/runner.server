const vscode = require('vscode');
const { LanguageClient, TransportKind } = require('vscode-languageclient/node');
const path = require('path');
const cp = require('child_process');

var startRunner = null;
var finishPromise = null;

/**
 * @param {vscode.ExtensionContext} context
 */
function activate(context) {

	(async() => {
		console.log("Aquire Dotnet!")

		// App requires .NET 8.0
		const commandRes = await vscode.commands.executeCommand('dotnet.acquire', { version: '8.0', requestingExtensionId: `${context.extension.packageJSON.publisher}.${context.extension.packageJSON.name}`, mode: "aspnetcore" });
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
					args: [ path.join(context.extensionPath, 'native', 'Runner.Language.Server.dll') ]
				},
				debug: {
					transport: TransportKind.stdio,
					command: dotnetPath,
					args: [ path.join(context.extensionPath, 'native', 'Runner.Language.Server.dll') ]
				}
			},
			{
				documentSelector: [ { language: "yaml" }, { language: "azure-pipelines" } ],
			}
		);

		var stopServer = new AbortController();

		var serverproc = cp.spawn(dotnetPath, [ path.join(context.extensionPath, 'native', 'Runner.Server.dll'), '--urls', 'http://*:0' ], { encoding: 'utf-8', killSignal: 'SIGINT', signal: stopServer.signal, windowsHide: true, stdio: 'pipe', shell: false });
		context.subscriptions.push({ dispose: () => stopServer.abort() })
		serverproc.addListener('exit', code => {
			console.log(code);
		});
		var address = null;
		serverproc.stdout.on('data', async (data) => {
			var sdata = data.asciiSlice();
			console.log(sdata)
			var i = sdata.indexOf("http://");
			if(i !== -1) {
				var end = sdata.indexOf('\n', i + 1);
				address = sdata.substring(i, end).replace("[::]", "localhost").replace("0.0.0.0", "localhost").trim();

				var panel = vscode.window.createWebviewPanel(
					"runner.server",
					"Runner Server",
					vscode.ViewColumn.Two,
					{
						enableScripts: true,
						// Without this we loose your webview position when the webview is in background
						retainContextWhenHidden: true
					}
				);

				const fullWebServerUri = await vscode.env.asExternalUri(
					vscode.Uri.parse(address)
				);

				var args = [ path.join(context.extensionPath, 'native', 'Runner.Client.dll'), 'startrunner', '--parallel', '4' ];
				if(address) {
					args.push('--server', address)
				}

				finishPromise = new Promise(onexit => {
					startRunner = cp.spawn(dotnetPath, args, { encoding: 'utf-8', windowsHide: true, stdio: 'pipe', shell: false, env: { ...process.env, RUNNER_CLIENT_EXIT_AFTER_ENTER: "1" } });
					startRunner.stdout.on('data', async (data) => {
						var sdata = data.asciiSlice();
						console.log(sdata)
					});
					startRunner.addListener('exit', code => {
						console.log(code);
						onexit();
					});
				})
				const cspSource = panel.webview.cspSource;
				// Get the content Uri
				const style = panel.webview.asWebviewUri(
					vscode.Uri.joinPath(context.extensionUri, 'style.css')
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
						<iframe src="${fullWebServerUri}"></iframe>
					</body>
				</html>`;
				context.subscriptions.push(panel);
			}
		});
		
		vscode.commands.registerCommand("runner.server.start-client", () => {
			var args = [ path.join(context.extensionPath, 'native', 'Runner.Client.dll'), '--interactive' ];
			if(address) {
				args.push('--server', address)
			}
			context.subscriptions.push(vscode.window.createTerminal("runner.client", dotnetPath, args))
		});

		vscode.commands.registerCommand("runner.server.runworkflow", (workflow) => {
			console.log(`runner.server.runjob {workflow}`)
			var args = [ path.join(context.extensionPath, 'native', 'Runner.Client.dll'), '-W', vscode.Uri.parse(workflow).fsPath ];
			if(address) {
				args.push('--server', address)
			}
			context.subscriptions.push(vscode.window.createTerminal("runner.client", dotnetPath, args))
		});
		vscode.commands.registerCommand("runner.server.runjob", (workflow, job) => {
			console.log(`runner.server.runjob {workflow}.{job}`)
			var args = [ path.join(context.extensionPath, 'native', 'Runner.Client.dll'), '-W', vscode.Uri.parse(workflow).fsPath, '-j', job ];
			if(address) {
				args.push('--server', address)
			}
			context.subscriptions.push(vscode.window.createTerminal("runner.client", dotnetPath, args))
		});

		context.subscriptions.push(client);
		client.start();
	})();
}

// this method is called when your extension is deactivated
async function deactivate() {
	if(finishPromise !== null) {
		startRunner.stdin.write("\n");
		await finishPromise;
	}
}
module.exports = {
	activate,
	deactivate
}
