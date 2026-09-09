#!/usr/bin/env node
import assert from 'node:assert/strict';
import fs from 'node:fs';
assert.equal(fs.existsSync('.github/workflows'), false, '.github/workflows must remain absent');
const dependabot = fs.readFileSync('.github/dependabot.yml','utf8');
assert.doesNotMatch(dependabot, /package-ecosystem:\s*["']?github-actions/);
console.log('Test-JPVGitHubActionsRetirement: PASS');
