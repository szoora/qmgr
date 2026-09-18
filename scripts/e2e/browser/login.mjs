export async function login(t, id, pw) {
  await t.goto('http://127.0.0.1:5003/login');
  await t.sleep(500);
  await t.eval('localStorage.clear(); sessionStorage.clear(); true');
  await t.goto('http://127.0.0.1:5003/login');
  await t.waitFor(`!!document.querySelector('input[type=text]')`); await t.sleep(1000);
  await t.setValue('input[type=text]', id);
  await t.clickText('Continue');
  if (!await t.waitFor(`!!document.querySelector('input[type=password]')`)) throw new Error('no password step: ' + await t.eval('document.body.innerText.slice(0,300)'));
  await t.sleep(400);
  await t.setValue('input[type=password]', pw);
  await t.clickText('Sign In', 'button');
  if (!await t.waitFor(`!location.pathname.startsWith('/login')`, 25000)) throw new Error('still on login: ' + await t.eval('document.body.innerText.slice(0,300)'));
  await t.sleep(1500);
}
