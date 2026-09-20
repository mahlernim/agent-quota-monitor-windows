import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

const source=fs.readFileSync(new URL('../web/app.js',import.meta.url),'utf8');
const calculation=source.match(/\/\/ PACE_CALC_START\s*([\s\S]*?)\s*\/\/ PACE_CALC_END/);
assert.ok(calculation,'pace calculation markers should be present');
const context={};
vm.runInNewContext(calculation[1]+';this.quotaPace=quotaPace',context);
const pace=context.quotaPace;
const now=Date.parse('2026-09-20T00:00:00Z');

const fiveHour={windowSeconds:18000,remaining:70,resetsAt:'2026-09-20T02:30:00Z'};
assert.deepEqual({...pace(fiveHour,true,now)},{available:true,timeRemaining:50,difference:20,direction:'under'});

const weekly={windowSeconds:604800,remaining:25,resetsAt:'2026-09-25T21:36:00Z'};
const weeklyPace=pace(weekly,true,now);
assert.equal(weeklyPace.available,true);
assert.ok(Math.abs(weeklyPace.timeRemaining-84.28571428571429)<1e-9);
assert.ok(Math.abs(weeklyPace.difference+59.28571428571429)<1e-9);
assert.equal(weeklyPace.direction,'over');

assert.equal(pace({...fiveHour,resetsAt:null},true,now).available,false);
assert.equal(pace({...fiveHour,resetsAt:'2026-09-19T23:59:00Z'},true,now).available,false);
assert.equal(pace({...fiveHour,resetsAt:'2026-09-20T06:00:00Z'},true,now).available,false);
assert.equal(pace(fiveHour,false,now).reason,'quota data is not live');
assert.equal(pace({windowSeconds:3600,remaining:50,resetsAt:'2026-09-20T00:30:00Z'},true,now),null);

console.log('pace calculation tests passed');
