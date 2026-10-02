import { translations } from './translations.js';

const supported = ['en', 'ko', 'ja', 'es'];
const preferenceKey = 'aqm-site-language';
const selector = document.querySelector('#language');

// Keep original text nodes so changing language never replaces links or SVGs.
const walker = document.createTreeWalker(document, NodeFilter.SHOW_TEXT);
const textNodes = [];
while (walker.nextNode()) {
  const node = walker.currentNode;
  if (!node.parentElement || node.parentElement.closest('script, style, option')) continue;
  const original = node.nodeValue;
  const key = original.trim();
  if (key) textNodes.push({ node, original, key });
}

const attributes = [];
for (const element of document.querySelectorAll('[alt], [aria-label], meta[name="description"], meta[property="og:title"], meta[property="og:description"]')) {
  for (const name of ['alt', 'aria-label', 'content']) {
    if (name === 'content' && element.tagName !== 'META') continue;
    const original = element.getAttribute(name);
    if (original) attributes.push({ element, name, original });
  }
}

function setLanguage(language, remember = false) {
  const locale = supported.includes(language) ? language : 'en';
  const dictionary = translations[locale] ?? {};
  for (const { node, original, key } of textNodes) {
    node.nodeValue = Object.hasOwn(dictionary, key)
      ? original.replace(key, () => dictionary[key])
      : original;
  }
  for (const { element, name, original } of attributes) {
    element.setAttribute(name, dictionary[original] ?? original);
  }
  document.documentElement.lang = locale;
  selector.value = locale;
  if (remember) {
    try { localStorage.setItem(preferenceKey, locale); } catch { /* Storage is optional. */ }
  }
}

let saved;
try { saved = localStorage.getItem(preferenceKey); } catch { /* Use browser preference. */ }
const browserLanguages = navigator.languages?.length ? navigator.languages : [navigator.language];
const detected = browserLanguages
  .map(language => String(language).toLowerCase().split(/[-_]/)[0])
  .find(language => supported.includes(language));
setLanguage(supported.includes(saved) ? saved : (detected ?? 'en'));
selector.addEventListener('change', () => setLanguage(selector.value, true));
document.querySelector('.language-control').hidden = false;
