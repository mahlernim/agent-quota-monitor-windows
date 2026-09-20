// Read-only official SDK bridge. No sessions, prompts, token files or auth writes.
import {pathToFileURL} from 'node:url';
import path from 'node:path';
import {execFileSync} from 'node:child_process';
import {normalizeQuotaPools} from './copilot_bridge_data.mjs';
const runtime = path.join(process.env.LOCALAPPDATA, 'QuotaDashboard/copilot-runtime');
let client;
const fail = (code, retryAfter=0) => Object.assign(new Error(code), {code, retryAfter});
try {
  const {CopilotClient, RuntimeConnection} = await import(pathToFileURL(path.join(runtime, 'node_modules/@github/copilot-sdk/dist/index.js')));
  // Pin one existing gh credential in memory for both quota and numeric identity.
  // The official gh CLI owns its credential store. Nothing is refreshed or saved here.
  const token=execFileSync(process.argv[3],['auth','token','--hostname','github.com'],{encoding:'utf8',windowsHide:true,timeout:12000,stdio:['ignore','pipe','ignore']}).trim();
  if (!token) throw fail('sign_in_required');
  client = new CopilotClient({connection:RuntimeConnection.forStdio({path:process.argv[2], args:['--no-auto-update','--no-remote','--no-remote-export']}), workingDirectory:runtime, logLevel:'none', gitHubToken:token, useLoggedInUser:false});
  await client.start();
  const status = await client.getAuthStatus();
  if (!status.isAuthenticated) throw fail('sign_in_required');
  // This reader is bound to the existing gh CLI account, not an arbitrary token/provider.
  if (!['token','env'].includes(status.authType) || status.host !== 'https://github.com') throw fail('copilot_auth_source_unsupported');
  await client.rpc.account.getQuota({});
  const {authInfo} = await client.rpc.account.getCurrentAuth();
  const response = await fetch('https://api.github.com/user', {redirect:'error', signal:AbortSignal.timeout(12000), headers:{Authorization:'Bearer '+token, Accept:'application/vnd.github+json','User-Agent':'QuotaDashboard'}});
  if (!response.ok) {
    const retry = response.headers.get('retry-after');
    const delay = retry ? (/^\d+$/.test(retry) ? Number(retry) : Math.max(0,(Date.parse(retry)-Date.now())/1000)) : 0;
    throw fail(response.status===429 || (response.status===403 && response.headers.get('x-ratelimit-remaining')==='0') ? 'rate_limited' : [401,403].includes(response.status) ? 'sign_in_required' : 'provider_http_'+response.status, Number.isFinite(delay)?delay:0);
  }
  const profile = await response.json();
  const user = authInfo.copilotUser;
  if (!Number.isSafeInteger(profile.id) || profile.id<=0 || user?.login?.toLowerCase()!==profile.login?.toLowerCase() || (status.login && profile.login?.toLowerCase()!==status.login.toLowerCase())) throw fail('identity_mismatch');
  const after = await client.getAuthStatus();
  if (after.login!==status.login || after.authType!==status.authType) throw fail('identity_changed');
  if (!user.quota_snapshots || typeof user.quota_snapshots!=='object') throw fail('schema_changed');
  // SDK 1.0.14 getQuota.resetDate can contain timestamp_utc. Use the provider's explicit quota reset instead.
  const pools = normalizeQuotaPools(user.quota_snapshots);
  const models = (await client.listModels()).map(m=>({id:m.id,name:m.name}));
  process.stdout.write(JSON.stringify({id:profile.id,login:profile.login,plan:user.copilot_plan,sku:user.access_type_sku,tokenBasedBilling:user.token_based_billing,reset:user.quota_reset_date_utc,pools,models}));
} catch (err) {
  // SDK exception text may include credential data. Emit only known public error codes.
  const code = typeof err.code==='string' && /^(sign_in_required|copilot_auth_source_unsupported|identity_changed|identity_mismatch|schema_changed|rate_limited|provider_http_\d+)$/.test(err.code) ? err.code : 'copilot_reader_failed';
  process.stdout.write(JSON.stringify({error:code,retryAfter:err.retryAfter || 0}));
  process.exitCode=1;
} finally {
  if (client) await client.stop();
}
