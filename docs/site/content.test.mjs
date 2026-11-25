import assert from 'node:assert/strict';
import { access, readdir, readFile } from 'node:fs/promises';
import test from 'node:test';
import { unified } from 'unified';
import remarkParse from 'remark-parse';
import remarkGfm from 'remark-gfm';
import { visit } from 'unist-util-visit';
import { getBasePath, pages } from './config.mjs';

const documents = await Promise.all(pages.map(async page => {
  const content = await readFile(new URL(`../${page.source}`, import.meta.url), 'utf8');
  return { ...page, content, tree: unified().use(remarkParse).use(remarkGfm).parse(content) };
}));

test('Should_MapRoutesToMarkdown_When_ReadingNavigation', () => {
  assert.ok(pages.some(page => page.route === '/guide/configuration/'));
  assert.ok(pages.some(page => page.route === '/guide/features/'));
  assert.ok(pages.every(page => page.group && page.description));
  assert.equal(new Set(pages.map(page => page.route)).size, pages.length);
  for (const page of pages) {
    assert.equal(page.source, page.route === '/' ? 'content/index.md' : `content${page.route.slice(0, -1)}.md`);
  }
});

test('Should_ContainOnlyPublishedMarkdown_When_DiscoveringContent', async () => {
  const files = await readdir(new URL('../content/', import.meta.url), { recursive: true });
  const actual = files.filter(file => /\.mdx?$/.test(file)).map(file => `content/${file.replaceAll('\\', '/')}`).sort();
  assert.deepEqual(actual, pages.map(page => page.source).sort());
  for (const document of documents) assert.doesNotMatch(document.content, /TreeSitter|tree-sitter/, document.source);
});

test('guides are not duplicated as top-level Markdown', async () => {
  const files = await readdir(new URL('../', import.meta.url));
  assert.deepEqual(files.filter(file => file.endsWith('.md') && file !== 'AGENTS.md').sort(), ['README.md']);
});

test('authored pages declare title and description metadata', () => {
  for (const document of documents) {
    assert.match(document.content, /^---\r?\n/, document.source);
    assert.match(document.content, /^title:\s*\S+/m, document.source);
    assert.match(document.content, /^description:\s*\S+/m, document.source);
  }
});

test('internal page links target published routes', () => {
  const routes = new Set(pages.map(page => page.route));
  for (const document of documents) {
    visit(document.tree, node => {
      if (node.type !== 'link' && node.type !== 'definition') return;
      if (/^(?:[a-z][a-z\d+.-]*:|\/\/)/i.test(node.url)) return;
      const target = new URL(node.url, `https://docs.local${document.route}`);
      const route = target.pathname.endsWith('/') ? target.pathname : `${target.pathname}/`;
      assert.ok(routes.has(route), `${document.source}: unpublished destination ${node.url}`);
    });
  }
});

test('uses explicit, project, and user-site base paths', () => {
  assert.equal(getBasePath({}), '');
  assert.equal(getBasePath({ DOCS_BASE_PATH: '/custom/' }), '/custom');
  assert.equal(getBasePath({ GITHUB_ACTIONS: 'true', GITHUB_REPOSITORY: 'owner/project' }), '/project');
  assert.equal(getBasePath({ GITHUB_ACTIONS: 'true', GITHUB_REPOSITORY: 'owner/owner.github.io' }), '');
  assert.equal(getBasePath({ DOCS_BASE_PATH: '', GITHUB_ACTIONS: 'true', GITHUB_REPOSITORY: 'owner/project' }), '');
  assert.throws(() => getBasePath({ DOCS_BASE_PATH: '../unsafe' }), /DOCS_BASE_PATH/);
});

test('JSON configuration examples parse', () => {
  let examples = 0;
  for (const document of documents) {
    visit(document.tree, 'code', node => {
      if (node.lang !== 'json') return;
      assert.doesNotThrow(() => JSON.parse(node.value), document.source);
      examples++;
    });
  }
  assert.ok(examples > 0);
});

test('dev and build cannot regenerate authored content', async () => {
  const { scripts } = JSON.parse(await readFile(new URL('../package.json', import.meta.url), 'utf8'));
  assert.ok(!scripts.content && !scripts.predev && !scripts.prebuild);
  await assert.rejects(access(new URL('./generate-content.mjs', import.meta.url)), { code: 'ENOENT' });
});