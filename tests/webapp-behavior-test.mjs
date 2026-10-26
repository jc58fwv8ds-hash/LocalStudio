import fs from 'node:fs';
import vm from 'node:vm';

const sourcePath = process.argv[2];
if (!sourcePath) throw new Error('app.js path is required');
const source = fs.readFileSync(sourcePath, 'utf8');
const storageValues = new Map();

class ClassList {
  constructor(owner) { this.owner = owner; }
  add(value) { if (!this.owner.className.split(/\s+/).includes(value)) this.owner.className = `${this.owner.className} ${value}`.trim(); }
  remove(value) { this.owner.className = this.owner.className.split(/\s+/).filter(x => x && x !== value).join(' '); }
}

class Element {
  constructor(tag = 'div') {
    this.tagName = tag.toUpperCase();
    this.children = [];
    this.listeners = new Map();
    this.dataset = {};
    this.className = '';
    this.classList = new ClassList(this);
    this.textContent = '';
    this.value = '';
    this.hidden = false;
    this.parent = null;
  }
  addEventListener(type, handler) { const list = this.listeners.get(type) || []; list.push(handler); this.listeners.set(type, list); }
  dispatch(type, extra = {}) { for (const handler of this.listeners.get(type) || []) handler({ type, preventDefault() {}, key: '', ...extra }); }
  append(...items) { for (const item of items) { item.parent = this; this.children.push(item); } }
  replaceChildren(...items) { this.children = []; this.append(...items); }
  replaceWith(item) {
    if (!this.parent) return;
    const index = this.parent.children.indexOf(this);
    if (index >= 0) { item.parent = this.parent; this.parent.children[index] = item; }
  }
  setAttribute(name, value) { this[name] = value; }
  focus() {}
  select() {}
}

function createRuntime() {
  const elements = new Map([
    ['task-form', new Element('form')],
    ['task-input', new Element('input')],
    ['task-list', new Element('ul')],
    ['empty-msg', new Element('p')],
    ['task-count', new Element('p')],
    ['clear-completed', new Element('button')]
  ]);
  const filters = ['all', 'active', 'completed'].map(value => {
    const button = new Element('button');
    button.dataset.filter = value;
    button.className = value === 'all' ? 'filter-btn active' : 'filter-btn';
    return button;
  });
  const document = {
    getElementById(id) { return elements.get(id); },
    querySelectorAll(selector) { if (selector === '.filter-btn') return filters; throw new Error(`unexpected selector: ${selector}`); },
    createElement(tag) { return new Element(tag); }
  };
  const context = vm.createContext({
    document,
    localStorage: {
      getItem(key) { return storageValues.has(key) ? storageValues.get(key) : null; },
      setItem(key, value) { storageValues.set(key, String(value)); }
    },
    crypto: { randomUUID: () => `id-${storageValues.size + 1}` },
    Date,
    Math,
    JSON,
    console
  });
  context.globalThis = context;
  vm.runInContext(source, context, { filename: sourcePath, timeout: 2000 });
  return { elements, filters };
}

function assert(condition, message) { if (!condition) throw new Error(message); }

let runtime = createRuntime();
const input = runtime.elements.get('task-input');
const form = runtime.elements.get('task-form');
const list = runtime.elements.get('task-list');
input.value = ' 論文を確認する ';
form.dispatch('submit');
assert(list.children.length === 1, 'add failed');
assert(list.children[0].children[1].textContent === '論文を確認する', 'trim or render failed');
assert(runtime.elements.get('task-count').textContent.includes('未完了 1件'), 'count failed');

list.children[0].children[1].dispatch('dblclick');
const editor = list.children[0].children[1];
editor.value = '検証結果をまとめる';
editor.dispatch('keydown', { key: 'Enter' });
assert(list.children[0].children[1].textContent === '検証結果をまとめる', 'keyboard edit failed');

list.children[0].children[1].dispatch('dblclick');
const cancelledEditor = list.children[0].children[1];
cancelledEditor.value = '保存してはいけない内容';
cancelledEditor.dispatch('keydown', { key: 'Escape' });
cancelledEditor.dispatch('blur');
assert(list.children[0].children[1].textContent === '検証結果をまとめる', 'escape cancel failed');

list.children[0].children[0].dispatch('click');
assert(list.children[0].className.includes('done'), 'toggle failed');
runtime.filters[1].dispatch('click');
assert(list.children.length === 0 && runtime.elements.get('empty-msg').hidden === false, 'active filter failed');
runtime.filters[2].dispatch('click');
assert(list.children.length === 1, 'completed filter failed');

runtime = createRuntime();
assert(runtime.elements.get('task-list').children.length === 1, 'persistence reload failed');
runtime.elements.get('clear-completed').dispatch('click');
assert(runtime.elements.get('task-list').children.length === 0, 'clear completed failed');
const reloadedInput = runtime.elements.get('task-input');
reloadedInput.value = '削除確認';
runtime.elements.get('task-form').dispatch('submit');
runtime.elements.get('task-list').children[0].children[2].dispatch('click');
assert(runtime.elements.get('task-list').children.length === 0, 'delete failed');

console.log('PASS: add, edit-enter, edit-escape, toggle, filters, persistence, clear-completed, delete');
