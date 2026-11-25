import path from 'node:path';
import { fileURLToPath } from 'node:url';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

const __dirname = path.dirname(fileURLToPath(import.meta.url));

export default defineConfig({
  plugins: [react()],
  define: {
    'process.env.NODE_ENV': JSON.stringify('production'),
  },
  build: {
    outDir: 'dist-widget',
    emptyOutDir: true,
    copyPublicDir: false,
    cssCodeSplit: false,
    lib: {
      entry: path.resolve(__dirname, 'src/widget/docsearch-widget.tsx'),
      name: 'DocSearchWidget',
      formats: ['iife'],
      fileName: () => 'docsearch-widget.js',
    },
  },
});
