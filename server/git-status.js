'use strict';

/**
 * Best-effort local git status lookup for a project directory.
 * Read-only, sandboxed, and safe to call frequently:
 *  - uses execFile with an argument array (never a shell string) so a
 *    project path can never be interpreted as a shell command
 *  - hard timeout so a hung/huge repo can't stall the polling loop
 *  - any failure (not a repo, git missing, permission denied) resolves to
 *    null rather than throwing — callers just skip showing git info
 */

const { execFile } = require('child_process');

const GIT_TIMEOUT_MS = 3000;

function run(args, cwd) {
  return new Promise((resolve) => {
    execFile('git', args, { cwd, timeout: GIT_TIMEOUT_MS, windowsHide: true }, (err, stdout) => {
      resolve(err ? null : stdout);
    });
  });
}

/** Returns { branch, changedCount } for a project path, or null if unavailable. */
async function getGitStatus(cwd) {
  if (!cwd) return null;

  const branchOut = await run(['branch', '--show-current'], cwd);
  if (branchOut === null) return null; // not a repo, git missing, or timed out

  const branch = branchOut.trim();
  if (!branch) return null; // detached HEAD or no commits yet — skip rather than guess

  const statusOut = await run(['status', '--porcelain'], cwd);
  if (statusOut === null) return null;

  const changedCount = statusOut.split('\n').filter((line) => line.trim().length > 0).length;

  return { branch, changedCount };
}

module.exports = { getGitStatus };
