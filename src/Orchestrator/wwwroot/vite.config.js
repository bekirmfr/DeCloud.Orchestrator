import { defineConfig } from 'vite';
import path from 'path';

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
  }
});
