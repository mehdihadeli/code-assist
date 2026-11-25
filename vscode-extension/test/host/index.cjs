const assert = require('node:assert/strict');
const vscode = require('vscode');
const manifest = require('../../package.json');

exports.run = async function run() {
  const extension = vscode.extensions.getExtension(`${manifest.publisher}.${manifest.name}`);
  assert.ok(extension, 'Development extension must be loaded.');
  await extension.activate();
  assert.equal(extension.isActive, true);
  const commands = await vscode.commands.getCommands(true);
  for (const command of manifest.contributes.commands) assert.ok(commands.includes(command.command), command.command);
  assert.equal(vscode.workspace.getConfiguration('codeAssist').get('server.mode'), 'managed');
  await vscode.commands.executeCommand('codeAssist.disconnect');
  console.log('CodeAssist real editor-host smoke passed: activation, native chat registration, commands, settings and disconnect. No inference.');
};