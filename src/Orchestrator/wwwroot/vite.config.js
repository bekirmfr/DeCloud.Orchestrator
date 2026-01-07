import { defineConfig } from 'vite';
import path from 'path';
import { copyFileSync, existsSync, mkdirSync } from 'fs';

export default defineConfig({
    // Base public path
    base: '/',

    // Build configuration
    build: {
        outDir: 'dist',
        assetsDir: 'assets',

        // Generate sourcemaps for production debugging
        sourcemap: true,

        // Optimize chunk splitting
        rollupOptions: {
            output: {
                manualChunks: {
                    // Vendor chunk for dependencies
                    'vendor-ethers': ['ethers'],
                    'vendor-appkit': ['@reown/appkit', '@reown/appkit-adapter-ethers']
                }
            }
        },

        // Target modern browsers
        target: 'es2020',

        // Minification
        minify: 'terser',
        terserOptions: {
            compress: {
                drop_console: false, // Keep console logs for production debugging
                drop_debugger: true
            }
        }
    },

    // Development server configuration
    server: {
        port: 3000,
        strictPort: false,

        // Proxy API requests to backend
        proxy: {
            '/api': {
                target: 'http://localhost:5050',
                changeOrigin: true,
                secure: false
            },
            '/ws': {
                target: 'ws://localhost:5050',
                ws: true
            }
        },

        // Hot Module Replacement
        hmr: {
            overlay: true
        }
    },

    // Preview server (for testing production build)
    preview: {
        port: 3000,
        strictPort: false,
        proxy: {
            '/api': {
                target: 'http://localhost:5050',
                changeOrigin: true,
                secure: false
            }
        }
    },

    // Resolve configuration
    resolve: {
        alias: {
            '@': path.resolve(__dirname, './src')
        }
    },

    // Optimize dependencies
    optimizeDeps: {
        include: ['ethers', '@reown/appkit', '@reown/appkit-adapter-ethers']
    },

    // Define global constants
    define: {
        // Can be overridden by environment variables
        __APP_VERSION__: JSON.stringify(process.env.npm_package_version || '2.0.0')
    },

    // Build hooks to copy additional files
    plugins: [
        {
            name: 'copy-terminal-html',
            closeBundle() {
                // Copy terminal.html to dist after build completes
                const sourceFile = path.resolve(__dirname, 'terminal.html');
                const destFile = path.resolve(__dirname, 'dist', 'terminal.html');

                if (existsSync(sourceFile)) {
                    // Ensure dist directory exists
                    const distDir = path.resolve(__dirname, 'dist');
                    if (!existsSync(distDir)) {
                        mkdirSync(distDir, { recursive: true });
                    }

                    // Copy the file
                    copyFileSync(sourceFile, destFile);
                    console.log('✓ Copied terminal.html to dist/');
                } else {
                    console.warn('⚠ terminal.html not found in wwwroot/');
                }
            }
        },
        {
            name: 'copy-file-browser-html',
            closeBundle() {
                // Copy file-browser.html to dist after build completes
                const sourceFile = path.resolve(__dirname, 'file-browser.html');
                const destFile = path.resolve(__dirname, 'dist', 'file-browser.html');

                if (existsSync(sourceFile)) {
                    // Ensure dist directory exists
                    const distDir = path.resolve(__dirname, 'dist');
                    if (!existsSync(distDir)) {
                        mkdirSync(distDir, { recursive: true });
                    }

                    // Copy the file
                    copyFileSync(sourceFile, destFile);
                    console.log('✓ Copied file-browser.html to dist/');
                } else {
                    console.warn('⚠ file-browser.html not found in wwwroot/');
                }
            }
        },
        {
            name: 'copy-sign-html',
            closeBundle() {
                // Copy file-browser.html to dist after build completes
                const sourceFile = path.resolve(__dirname, 'sign.html');
                const destFile = path.resolve(__dirname, 'dist', 'sign.html');

                if (existsSync(sourceFile)) {
                    // Ensure dist directory exists
                    const distDir = path.resolve(__dirname, 'dist');
                    if (!existsSync(distDir)) {
                        mkdirSync(distDir, { recursive: true });
                    }

                    // Copy the file
                    copyFileSync(sourceFile, destFile);
                    console.log('✓ Copied sign.html to dist/');
                } else {
                    console.warn('⚠ sign.html not found in wwwroot/');
                }
            }
        }
    ]
});