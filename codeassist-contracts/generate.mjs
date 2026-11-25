import fs from 'node:fs';
import openapiTS, { astToString } from 'openapi-typescript';

const source = new URL('./openapi.json', import.meta.url);
const output = new URL('./index.d.ts', import.meta.url);
const text = astToString(await openapiTS(source));
verify(output, text);
const document = JSON.parse(fs.readFileSync(source, 'utf8'));
const bindings = JSON.parse(fs.readFileSync(new URL('./csharp-bindings.json', import.meta.url), 'utf8'));
const records = Object.entries(bindings).map(([name, parameters]) => {
  const schema = document.components.schemas[name];
  if (!schema || parameters.length !== Object.keys(schema.properties).length) throw new Error(`Invalid constructor mapping: ${name}`);
  const values = parameters.map(property => {
    const item = schema.properties[property];
    const types = Array.isArray(item.type) ? item.type : [item.type];
    const nullable = types.includes('null');
    const type = item.format === 'uuid' ? 'Guid' : item.format === 'date-time' ? 'DateTimeOffset' :
      types.includes('integer') ? 'int' : types.includes('boolean') ? 'bool' : types.includes('string') ? 'string' : undefined;
    if (!type) throw new Error(`Unsupported C# scalar: ${name}.${property}`);
    const optional = !schema.required?.includes(property);
    if (optional && !nullable) throw new Error(`Explicit C# default required: ${name}.${property}`);
    return `${type}${nullable ? '?' : ''} ${property[0].toUpperCase() + property.slice(1)}${optional ? ' = null' : ''}`;
  });
  return `public sealed record ${name}(${values.join(', ')});`;
});
verify(new URL('../src/CodeAssist.Contracts/Generated/SessionContracts.g.cs', import.meta.url),
  `#nullable enable\n\nnamespace CodeAssist.Contracts;\n\n${records.join('\n\n')}\n`);
console.log('Contract bindings verified.');

function verify(target, contents) {
  if (process.argv.includes('--check')) {
    if (!fs.existsSync(target) || fs.readFileSync(target, 'utf8').replaceAll('\r\n', '\n') !== contents)
      throw new Error('Generated contracts are stale. Run npm run generate from codeassist-contracts.');
  } else if (!fs.existsSync(target) || fs.readFileSync(target, 'utf8') !== contents) {
    fs.mkdirSync(new URL('.', target), { recursive: true });
    fs.writeFileSync(target, contents);
  }
}