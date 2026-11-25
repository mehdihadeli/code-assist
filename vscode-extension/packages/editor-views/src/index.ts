import * as vscode from "vscode";

export function createConnectionStatus(): vscode.StatusBarItem {
  const status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 20);
  status.command = "codeAssist.connect";
  status.text = "$(plug) CodeAssist";
  status.tooltip = "CodeAssist: Disconnected";
  status.show();
  return status;
}

export function untrustedMarkdown(text: string): vscode.MarkdownString {
  const markdown = new vscode.MarkdownString(text);
  markdown.isTrusted = false;
  markdown.supportHtml = false;
  return markdown;
}