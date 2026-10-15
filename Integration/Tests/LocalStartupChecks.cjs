const assert = require('node:assert/strict');
const fs = require('node:fs');

const profiles = JSON.parse(fs.readFileSync('Properties/launchSettings.json', 'utf8')).profiles;
const config = JSON.parse(fs.readFileSync('appsettings.example.json', 'utf8'));
const local = config.InGame.DraftServers;
const endpoints = new Set(Object.values(profiles)[0].applicationUrl.split(';').map(url => new URL(url).origin));
assert(endpoints.has(new URL(local.BackendUrl).origin), 'dotnet run must listen on the API address passed to draft servers');
assert(endpoints.has(new URL(local.WebsiteUrl).origin), 'dotnet run must listen on the embedded website address');
assert.equal(new URL(local.WebsiteUrl).protocol, 'https:');
console.log('PASS: default launch profile serves both configured in-game addresses');
