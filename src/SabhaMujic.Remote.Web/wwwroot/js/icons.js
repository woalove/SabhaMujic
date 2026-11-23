// @ts-check
// Outline icon set (24×24, 1.75 px strokes, round caps) built as inline SVG through the DOM — no innerHTML, no files.
// Markup can ask for one with <span data-icon="name"></span>; hydrateIcons() fills those in.

const SVG = 'http://www.w3.org/2000/svg';

/**
 * Each icon: stroked paths, plus optional filled paths (`fill`) for solid glyphs such as the transport controls.
 * @type {Record<string, { stroke?: string[], fill?: string[], circles?: [number, number, number][] }>}
 */
const ICONS = {
  mark: { stroke: ['M9 18V6l11-2v12'], circles: [[6.5, 18, 2.5], [17.5, 16, 2.5]] },
  now: { stroke: ['M10 8.8v6.4l5.2-3.2z'], circles: [[12, 12, 9]] },
  queue: { stroke: ['M9 6h11', 'M9 12h11', 'M9 18h11', 'M4.5 6h.01', 'M4.5 12h.01', 'M4.5 18h.01'] },
  library: { stroke: ['M4.5 4.5h4v15h-4z', 'M10.5 4.5h4v15h-4z', 'M16 5.6l3.4-.9 2.4 14.4-3.4.9z'] },
  playlists: { stroke: ['M4 6h11', 'M4 11h11', 'M4 16h6', 'M19 16V6.5l2.5-.8'], circles: [[17, 16.5, 2]] },
  upload: { stroke: ['M12 15V4', 'M7.5 8.5L12 4l4.5 4.5', 'M5 15v3.5A1.5 1.5 0 0 0 6.5 20h11a1.5 1.5 0 0 0 1.5-1.5V15'] },
  sun: { stroke: ['M12 2.5v2', 'M12 19.5v2', 'M4.6 4.6l1.4 1.4', 'M18 18l1.4 1.4', 'M2.5 12h2', 'M19.5 12h2', 'M4.6 19.4L6 18', 'M18 6l1.4-1.4'], circles: [[12, 12, 4]] },
  moon: { stroke: ['M20.5 14.2A8.5 8.5 0 1 1 9.8 3.5a6.6 6.6 0 0 0 10.7 10.7z'] },
  'chevron-down': { stroke: ['M6 9l6 6 6-6'] },
  'chevron-right': { stroke: ['M9 6l6 6-6 6'] },
  play: { fill: ['M8 5.5v13l10.5-6.5z'] },
  pause: { fill: ['M7 5h3.6v14H7z', 'M13.4 5H17v14h-3.6z'] },
  prev: { fill: ['M18.5 18.5L9 12l9.5-6.5z'], stroke: ['M6 5.5v13'] },
  next: { fill: ['M5.5 5.5L15 12l-9.5 6.5z'], stroke: ['M18 5.5v13'] },
  stop: { fill: ['M6.5 6.5h11v11h-11z'] },
  volume: { stroke: ['M4 9.5h3.5L12 5.5v13l-4.5-4H4z', 'M15.5 9a4.2 4.2 0 0 1 0 6', 'M18.2 6.5a8 8 0 0 1 0 11'] },
  muted: { stroke: ['M4 9.5h3.5L12 5.5v13l-4.5-4H4z', 'M16 9.5l5 5', 'M21 9.5l-5 5'] },
  user: { stroke: ['M5 20a7 7 0 0 1 14 0'], circles: [[12, 8.5, 3.8]] },
  clock: { stroke: ['M12 7.5V12l3 2'], circles: [[12, 12, 8.5]] },
  plus: { stroke: ['M12 5v14', 'M5 12h14'] },
  'arrow-up': { stroke: ['M12 19V5', 'M6.5 10.5L12 5l5.5 5.5'] },
  'arrow-down': { stroke: ['M12 5v14', 'M6.5 13.5L12 19l5.5-5.5'] },
  trash: { stroke: ['M4.5 7h15', 'M9.5 7V4.5h5V7', 'M6.5 7l1 13h9l1-13'] },
  close: { stroke: ['M6 6l12 12', 'M18 6L6 18'] },
  loop: { stroke: ['M4 12a6 6 0 0 1 6-6h8', 'M15 3l3 3-3 3', 'M20 12a6 6 0 0 1-6 6H6', 'M9 21l-3-3 3-3'] },
  'play-next': { stroke: ['M4 6h10', 'M4 11h10', 'M4 16h6', 'M15 13.5v6l5-3z'] },
  signout: { stroke: ['M14 4h4.5A1.5 1.5 0 0 1 20 5.5v13a1.5 1.5 0 0 1-1.5 1.5H14', 'M10 8l-4 4 4 4', 'M6 12h10'] },
};

/**
 * Creates an icon element (decorative: aria-hidden; label the button that holds it).
 * @param {string} name
 * @param {number} [size]
 * @returns {SVGSVGElement}
 */
export function icon(name, size = 24) {
  const spec = ICONS[name];
  if (!spec) throw new Error(`Unknown icon ${name}`);
  const svg = document.createElementNS(SVG, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('width', String(size));
  svg.setAttribute('height', String(size));
  svg.setAttribute('aria-hidden', 'true');
  svg.setAttribute('focusable', 'false');
  svg.setAttribute('class', `icon icon-${name}`);
  for (const d of spec.stroke ?? []) svg.append(path(d, false));
  for (const [cx, cy, r] of spec.circles ?? []) {
    const circle = document.createElementNS(SVG, 'circle');
    circle.setAttribute('cx', String(cx));
    circle.setAttribute('cy', String(cy));
    circle.setAttribute('r', String(r));
    stroke(circle);
    svg.append(circle);
  }
  for (const d of spec.fill ?? []) svg.append(path(d, true));
  return svg;
}

/**
 * Replaces the content of every [data-icon] element inside `root` with its icon.
 * @param {ParentNode} [root]
 */
export function hydrateIcons(root = document) {
  for (const el of root.querySelectorAll('[data-icon]')) {
    if (el instanceof HTMLElement && el.dataset.icon) el.replaceChildren(icon(el.dataset.icon, Number(el.dataset.size) || 24));
  }
}

/**
 * Swaps the icon inside a button (or other element) in place.
 * @param {Element} host
 * @param {string} name
 * @param {number} [size]
 */
export function setIcon(host, name, size = 24) {
  host.replaceChildren(icon(name, size));
}

/** @param {string} d @param {boolean} filled */
function path(d, filled) {
  const p = document.createElementNS(SVG, 'path');
  p.setAttribute('d', d);
  if (filled) {
    p.setAttribute('fill', 'currentColor');
    p.setAttribute('stroke', 'none');
  } else {
    stroke(p);
  }
  return p;
}

/** @param {SVGElement} el */
function stroke(el) {
  el.setAttribute('fill', 'none');
  el.setAttribute('stroke', 'currentColor');
  el.setAttribute('stroke-width', '1.75');
  el.setAttribute('stroke-linecap', 'round');
  el.setAttribute('stroke-linejoin', 'round');
}
