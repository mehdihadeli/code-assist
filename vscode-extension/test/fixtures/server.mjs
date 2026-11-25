import { createServer } from 'node:http';

if (process.argv.slice(2).join(' ') !== '--listen 127.0.0.1:0') process.exit(2);
const server = createServer((request, response) => {
  if (request.headers.authorization !== `Bearer ${process.env.Host__Listen__Http__Token}`) {
    response.writeHead(401).end(); return;
  }
  response.end(JSON.stringify({ application: 'code-assist', protocol: '1', status: 'ready' }));
});
server.listen(0, '127.0.0.1', () => {
  const ready = JSON.stringify({ kind: 'ready', protocol: '1', endpoint: `http://127.0.0.1:${server.address().port}` });
  process.stdout.write(ready.slice(0, 10));
  setImmediate(() => console.log(ready.slice(10)));
});