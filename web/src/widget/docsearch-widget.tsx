import { StrictMode } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { DocSearchWidgetApp } from './DocSearchWidgetApp';
import { parseFontBaseUrl } from './widgetClient';
import widgetCss from './widget.css?inline';

const OBSERVED_ATTRIBUTES = [
  'api-url',
  'token',
  'collection-ids',
  'widget-title',
  'placeholder',
  'font-base-url',
] as const;

const UPGRADABLE_PROPERTIES = [
  'token',
  'apiUrl',
  'collectionIds',
  'widgetTitle',
  'placeholder',
  'fontBaseUrl',
] as const;

type WidgetPropertyName = (typeof UPGRADABLE_PROPERTIES)[number];

const VAZIRMATN_WEIGHTS: ReadonlyArray<{ file: string; weight: string }> = [
  { file: 'Vazirmatn-Regular.woff2', weight: '400' },
  { file: 'Vazirmatn-Medium.woff2', weight: '500' },
  { file: 'Vazirmatn-SemiBold.woff2', weight: '600' },
  { file: 'Vazirmatn-Bold.woff2', weight: '700' },
];

const loadedFontBases = new Set<string>();

function loadVazirmatnFromBase(rawFontBaseUrl: string): void {
  const safeBase = parseFontBaseUrl(rawFontBaseUrl);
  if (!safeBase || loadedFontBases.has(safeBase)) {
    return;
  }

  if (
    typeof FontFace !== 'function' ||
    typeof document === 'undefined' ||
    !document.fonts ||
    typeof document.fonts.add !== 'function'
  ) {
    return;
  }

  loadedFontBases.add(safeBase);

  for (const variant of VAZIRMATN_WEIGHTS) {
    try {
      const face = new FontFace(
        'Vazirmatn',
        `url("${safeBase}/${variant.file}") format("woff2")`,
        {
          weight: variant.weight,
          style: 'normal',
          display: 'swap',
        },
      );
      document.fonts.add(face);
      void face.load().catch(() => {
        // Ignore font fetch failures (e.g. offline/CORS) and fall back to system Persian fonts.
      });
    } catch {
      // Ignore FontFace construction failures in restricted environments.
    }
  }
}

export class DocSearchWidgetElement extends HTMLElement {
  static get observedAttributes(): readonly string[] {
    return OBSERVED_ATTRIBUTES;
  }

  #root: Root | null = null;
  #mountNode: HTMLDivElement | null = null;
  #styleEl: HTMLStyleElement | null = null;
  #token = '';
  #isConnected = false;

  get token(): string {
    return this.#token;
  }

  // Dynamic JS assignment stores the JWT only in private memory, never in a DOM attribute.
  set token(value: string) {
    const next = typeof value === 'string' ? value.trim() : '';
    if (this.#token !== next) {
      this.#token = next;
      this.#render();
    }
  }

  get apiUrl(): string {
    return this.getAttribute('api-url') ?? '';
  }

  set apiUrl(value: string) {
    const next = typeof value === 'string' ? value.trim() : '';
    this.#syncStringAttribute('api-url', next);
  }

  get collectionIds(): string {
    return this.getAttribute('collection-ids') ?? '';
  }

  set collectionIds(value: string | readonly string[]) {
    const next = Array.isArray(value)
      ? value
          .map((item) => String(item).trim())
          .filter(Boolean)
          .join(',')
      : typeof value === 'string'
        ? value.trim()
        : '';
    this.#syncStringAttribute('collection-ids', next);
  }

  get widgetTitle(): string {
    return this.getAttribute('widget-title') ?? '';
  }

  set widgetTitle(value: string) {
    const next = typeof value === 'string' ? value.trim() : '';
    this.#syncStringAttribute('widget-title', next);
  }

  get placeholder(): string {
    return this.getAttribute('placeholder') ?? '';
  }

  set placeholder(value: string) {
    const next = typeof value === 'string' ? value.trim() : '';
    this.#syncStringAttribute('placeholder', next);
  }

  get fontBaseUrl(): string {
    return this.getAttribute('font-base-url') ?? '';
  }

  set fontBaseUrl(value: string) {
    const next = typeof value === 'string' ? value.trim() : '';
    this.#syncStringAttribute('font-base-url', next);
  }

  connectedCallback(): void {
    this.#isConnected = true;

    const shadow = this.shadowRoot ?? this.attachShadow({ mode: 'open' });

    if (!this.#styleEl || this.#styleEl.parentNode !== shadow) {
      const existingStyle = shadow.querySelector('style[data-docsearch-widget-style]');
      if (existingStyle instanceof HTMLStyleElement) {
        this.#styleEl = existingStyle;
      } else {
        this.#styleEl = document.createElement('style');
        this.#styleEl.setAttribute('data-docsearch-widget-style', 'true');
        this.#styleEl.textContent = widgetCss;
        shadow.appendChild(this.#styleEl);
      }
    }

    if (!this.#mountNode || this.#mountNode.parentNode !== shadow) {
      const existingMount = shadow.querySelector('div.ds-widget-root');
      if (existingMount instanceof HTMLDivElement) {
        this.#mountNode = existingMount;
      } else {
        this.#mountNode = document.createElement('div');
        this.#mountNode.setAttribute('dir', 'rtl');
        this.#mountNode.setAttribute('lang', 'fa');
        this.#mountNode.className = 'ds-widget-root';
        shadow.appendChild(this.#mountNode);
      }
    }

    this.#upgradeAllProperties();
    loadVazirmatnFromBase(this.fontBaseUrl);
    this.#render();
  }

  disconnectedCallback(): void {
    this.#isConnected = false;
    this.#root?.unmount();
    this.#root = null;
  }

  attributeChangedCallback(name: string, oldValue: string | null, newValue: string | null): void {
    if (oldValue === newValue) {
      return;
    }

    if (name === 'token') {
      const next = (newValue ?? '').trim();
      if (this.#token !== next) {
        this.#token = next;
      }
    }

    if (name === 'font-base-url' && newValue) {
      loadVazirmatnFromBase(newValue);
    }

    this.#render();
  }

  #syncStringAttribute(attrName: string, nextValue: string): void {
    if (!nextValue) {
      if (this.hasAttribute(attrName)) {
        this.removeAttribute(attrName);
      }
      return;
    }
    if (this.getAttribute(attrName) !== nextValue) {
      this.setAttribute(attrName, nextValue);
    }
  }

  #upgradeAllProperties(): void {
    for (const prop of UPGRADABLE_PROPERTIES) {
      this.#upgradeProperty(prop);
    }
  }

  #upgradeProperty(prop: WidgetPropertyName): void {
    if (Object.prototype.hasOwnProperty.call(this, prop)) {
      const record = this as unknown as Record<string, unknown>;
      const value = record[prop];
      delete record[prop];
      record[prop] = value;
    }
  }

  #render(): void {
    if (!this.#isConnected || !this.#mountNode) {
      return;
    }

    if (!this.#root) {
      this.#root = createRoot(this.#mountNode);
    }

    this.#root.render(
      <StrictMode>
        <DocSearchWidgetApp
          apiUrl={this.apiUrl}
          token={this.#token}
          collectionIds={this.collectionIds}
          widgetTitle={this.widgetTitle}
          placeholder={this.placeholder}
        />
      </StrictMode>,
    );
  }
}

export function registerDocSearchWidget(): void {
  if (typeof customElements !== 'undefined' && !customElements.get('docsearch-widget')) {
    customElements.define('docsearch-widget', DocSearchWidgetElement);
  }
}

registerDocSearchWidget();
