// @ts-check
// DOM helpers. Data only ever reaches the page through textContent / attributes: no innerHTML anywhere.

/**
 * @typedef {object} Props
 * @property {string} [class]
 * @property {string} [text]
 * @property {Record<string, string | number | boolean | null | undefined>} [attrs]
 * @property {Record<string, (event: Event) => void>} [on]
 */

/**
 * Creates an element. Children that are strings become text nodes.
 * @template {keyof HTMLElementTagNameMap} K
 * @param {K} tag
 * @param {Props} [props]
 * @param {...(Node | string | null | undefined | false)} children
 * @returns {HTMLElementTagNameMap[K]}
 */
export function h(tag, props = {}, ...children) {
  const el = document.createElement(tag);
  if (props.class) el.className = props.class;
  if (props.text !== undefined) el.textContent = props.text;
  for (const [name, value] of Object.entries(props.attrs ?? {})) {
    if (value === false || value === null || value === undefined) continue;
    el.setAttribute(name, value === true ? '' : String(value));
  }
  for (const [name, handler] of Object.entries(props.on ?? {})) el.addEventListener(name, handler);
  for (const child of children) {
    if (child === null || child === undefined || child === false) continue;
    el.append(typeof child === 'string' ? document.createTextNode(child) : child);
  }
  return el;
}

/**
 * Typed getElementById that fails loudly when the markup and the code disagree.
 * @template {HTMLElement} T
 * @param {string} id
 * @param {new (...args: any[]) => T} type
 * @returns {T}
 */
export function byId(id, type) {
  const el = document.getElementById(id);
  if (!(el instanceof type)) throw new Error(`#${id} is missing or not a ${type.name}`);
  return el;
}

/** @param {Element} el */
export function clear(el) {
  el.replaceChildren();
}
