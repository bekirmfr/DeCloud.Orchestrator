# DeCloud Design System

## Overview
This document outlines the DeCloud design system to ensure visual consistency across all pages and components.

## Design Tokens
All design tokens are centralized in `design-tokens.css`. This file should be imported before any page-specific styles.

### Color Palette

#### Background Colors
- `--bg-deep: #0a0b0f` - Deepest background (body)
- `--bg-primary: #12141a` - Primary surfaces (cards, modals)
- `--bg-secondary: #1a1d26` - Secondary surfaces (inputs, nested cards)
- `--bg-elevated: #22262f` - Elevated surfaces (hover states)
- `--bg-hover: #2a2f3a` - Interactive hover states

#### Accent Colors
- `--accent-primary: #00d4aa` - Primary brand color (teal/cyan)
- `--accent-secondary: #00a8ff` - Secondary brand color (blue)
- `--accent-tertiary: #8b5cf6` - Tertiary accent (purple)
- `--accent-warning: #f59e0b` - Warning states (amber)
- `--accent-danger: #ef4444` - Error/danger states (red)
- `--accent-success: #10b981` - Success states (green)

#### Text Colors
- `--text-primary: #f0f2f5` - Primary text
- `--text-secondary: #9ca3af` - Secondary text, labels
- `--text-muted: #6b7280` - Muted text, placeholders

#### Border Colors
- `--border-subtle: rgba(255,255,255,0.06)` - Subtle borders
- `--border-default: rgba(255,255,255,0.1)` - Default borders
- `--border-hover: rgba(255,255,255,0.15)` - Hover state borders

### Typography

#### Font Families
- `--font-display: 'Outfit'` - Display text, headings, UI
- `--font-mono: 'JetBrains Mono'` - Code, monospace content

**Google Fonts Import:**
```html
<link href="https://fonts.googleapis.com/css2?family=JetBrains+Mono:wght@400;500;600&family=Outfit:wght@300;400;500;600;700&display=swap" rel="stylesheet">
```

#### Font Sizes
- `--font-xs: 11px`
- `--font-sm: 12px`
- `--font-base: 13px`
- `--font-md: 14px`
- `--font-lg: 16px`
- `--font-xl: 18px`
- `--font-2xl: 20px`
- `--font-3xl: 24px`
- `--font-4xl: 28px`
- `--font-5xl: 32px`

#### Font Weights
- `--font-normal: 400`
- `--font-medium: 500`
- `--font-semibold: 600`
- `--font-bold: 700`

### Spacing Scale
- `--space-xs: 4px`
- `--space-sm: 8px`
- `--space-md: 12px`
- `--space-lg: 16px`
- `--space-xl: 20px`
- `--space-2xl: 24px`
- `--space-3xl: 32px`
- `--space-4xl: 40px`

### Border Radius
- `--radius-sm: 6px` - Small elements (tags, badges)
- `--radius-md: 10px` - Medium elements (buttons, inputs)
- `--radius-lg: 16px` - Large elements (cards)
- `--radius-xl: 24px` - Extra large elements (modals)
- `--radius-full: 9999px` - Circular (pills, dots)

### Transitions
- `--transition-fast: 150ms ease` - Quick interactions
- `--transition-normal: 250ms ease` - Standard transitions
- `--transition-slow: 400ms cubic-bezier(0.16, 1, 0.3, 1)` - Smooth, pronounced transitions

### Shadows
- `--shadow-sm: 0 2px 8px rgba(0, 0, 0, 0.2)`
- `--shadow-md: 0 4px 20px rgba(0, 0, 0, 0.3)`
- `--shadow-lg: 0 8px 30px rgba(0, 0, 0, 0.4)`
- `--shadow-xl: 0 20px 60px rgba(0, 0, 0, 0.5)`
- `--shadow-glow-primary: 0 0 20px var(--glow-primary)`
- `--shadow-glow-secondary: 0 0 20px var(--glow-secondary)`

### Z-Index Scale
- `--z-base: 0`
- `--z-dropdown: 100`
- `--z-sticky: 500`
- `--z-fixed: 1000`
- `--z-modal-backdrop: 2000`
- `--z-modal: 2001`
- `--z-popover: 3000`
- `--z-toast: 4000`
- `--z-tooltip: 5000`

## Common Components

### Buttons
The design system provides standard button styles:

```html
<!-- Primary Button -->
<button class="btn btn-primary">Primary Action</button>

<!-- Secondary Button -->
<button class="btn btn-secondary">Secondary Action</button>
```

### Cards
Standard card component:

```html
<div class="card">
    <div class="card-header">
        <h2 class="card-title">Card Title</h2>
    </div>
    <div class="card-body">
        Card content goes here
    </div>
</div>
```

### Status Indicators
Consistent status dots across all pages:

```html
<span class="status-dot online"></span> <!-- Green -->
<span class="status-dot warning"></span> <!-- Amber -->
<span class="status-dot offline"></span> <!-- Red -->
<span class="status-dot checking"></span> <!-- Gray, animated -->
```

### Badges
```html
<span class="badge badge-primary">Primary</span>
<span class="badge badge-secondary">Secondary</span>
<span class="badge badge-warning">Warning</span>
<span class="badge badge-danger">Danger</span>
```

### Form Inputs
```html
<label class="form-label">Input Label</label>
<input type="text" class="form-input" placeholder="Placeholder text">
```

## Background Effects

For pages with decorative backgrounds:

```html
<!-- Grid pattern -->
<div class="bg-grid"></div>

<!-- Glow effects -->
<div class="bg-glow bg-glow-1"></div>
<div class="bg-glow bg-glow-2"></div>
```

## Usage Guidelines

### Importing the Design System

All pages should import the design tokens CSS file:

```html
<head>
    <!-- Google Fonts -->
    <link rel="preconnect" href="https://fonts.googleapis.com">
    <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
    <link href="https://fonts.googleapis.com/css2?family=JetBrains+Mono:wght@400;500;600&family=Outfit:wght@300;400;500;600;700&display=swap" rel="stylesheet">
    
    <!-- Design System -->
    <link rel="stylesheet" href="/design-tokens.css">
    
    <!-- Page-specific styles -->
    <link rel="stylesheet" href="/your-page-styles.css">
</head>
```

### Creating New Pages

When creating new pages:

1. **Import design tokens first** - Always import `design-tokens.css` before page-specific styles
2. **Use CSS variables** - Reference design tokens using CSS variables (`var(--accent-primary)`)
3. **Follow naming conventions** - Use the established naming patterns for consistency
4. **Reuse components** - Utilize the pre-built component classes when possible
5. **Match the art style** - Keep the dark theme, teal/cyan accents, and modern aesthetic

### Color Usage Guidelines

- **Primary Accent** (`--accent-primary` / teal): Use for primary actions, active states, success indicators
- **Secondary Accent** (`--accent-secondary` / blue): Use for informational elements, secondary actions
- **Warning** (`--accent-warning` / amber): Use for warnings, caution states
- **Danger** (`--accent-danger` / red): Use for errors, delete actions, critical states
- **Success** (`--accent-success` / green): Alternative success color (use sparingly)

### Typography Guidelines

- **Headings**: Use Outfit font, bold weights (600-700)
- **Body Text**: Use Outfit font, normal-medium weights (400-500)
- **Code/Data**: Use JetBrains Mono for code snippets, file paths, IDs, technical data
- **Buttons/UI**: Use Outfit font, semibold weight (600)

## Files Updated

The following files have been updated to use the design system:

### Orchestrator (Main App)
- ✅ `index.html` - Main dashboard
- ✅ `styles.css` - Main stylesheet (now imports design-tokens.css)
- ✅ `terminal.html` - Terminal interface
- ✅ `sign.html` - Node authorization page
- ✅ `file-browser.html` - SFTP file browser

### NodeAgent (VM Templates)
- ✅ `relay-vm/dashboard.html` - Relay VM dashboard
- ✅ `relay-vm/dashboard.css` - Relay VM styles
- ✅ `general-vm/index.html` - General VM welcome page

## Maintenance

To maintain consistency:

1. **Always use design tokens** - Never hardcode colors or values
2. **Update tokens centrally** - If design changes are needed, update `design-tokens.css`
3. **Document new patterns** - Add new reusable patterns to this file
4. **Review regularly** - Periodically audit pages for consistency
5. **Test across pages** - Verify changes work across all pages

## Version History

- **v1.0.0** (Current) - Initial design system implementation
  - Centralized design tokens
  - Standardized color palette (teal/cyan theme)
  - Unified typography (Outfit + JetBrains Mono)
  - Consistent component styles
  - Updated all existing pages

---

**Last Updated**: 2026-01-31  
**Maintained By**: DeCloud Development Team
