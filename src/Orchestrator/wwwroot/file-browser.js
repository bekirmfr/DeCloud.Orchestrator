/**
 * DeCloud File Browser - SFTP Client
 * Secure file transfer over WebSocket using SFTP protocol
 * 
 * @version 1.0.0
 */

// ============================================
// STATE
// ============================================

let socket = null;
let isConnected = false;
let currentPath = '/home';
let files = [];
let selectedFile = null;
let pathHistory = [];
let historyIndex = -1;

// Transfer management
let activeTransfers = new Map();
let pendingChunks = new Map();

// Connection params from URL
const params = new URLSearchParams(window.location.search);
const connectionParams = {
    vmId: params.get('vmId') || '',
    nodeIp: params.get('nodeIp') || '',
    nodePort: params.get('nodePort') || '5100',
    vmIp: params.get('vmIp') || '',
    username: params.get('user') || 'ubuntu',
    password: params.get('password') || ''
};

// ============================================
// INITIALIZATION
// ============================================

document.addEventListener('DOMContentLoaded', () => {
    // Pre-fill connection form from URL params
    if (connectionParams.vmIp) {
        document.getElementById('connect-ip').value = connectionParams.vmIp;
    }
    if (connectionParams.username) {
        document.getElementById('connect-user').value = connectionParams.username;
    }
    if (connectionParams.password) {
        document.getElementById('connect-password').value = connectionParams.password;
    }

    // Update VM badge
    if (connectionParams.vmId) {
        document.getElementById('vm-badge').textContent = connectionParams.vmId;
    }

    // Auto-connect if we have all params
    if (connectionParams.nodeIp && connectionParams.vmIp && connectionParams.password) {
        connect();
    }

    // Setup drag & drop
    setupDragAndDrop();

    // Setup keyboard shortcuts
    setupKeyboardShortcuts();

    // Close context menu on click outside
    document.addEventListener('click', () => {
        document.getElementById('context-menu').classList.remove('visible');
    });
});

// ============================================
// CONNECTION
// ============================================

function connect() {
    const ip = document.getElementById('connect-ip').value || connectionParams.vmIp;
    const user = document.getElementById('connect-user').value || connectionParams.username;
    const password = document.getElementById('connect-password').value || connectionParams.password;

    if (!ip) {
        showToast('Please enter VM IP address', 'error');
        return;
    }

    if (!password) {
        showToast('Please enter password', 'error');
        return;
    }

    // Build WebSocket URL
    const nodeHost = connectionParams.nodeIp || window.location.hostname;
    const nodePort = connectionParams.nodePort || '5100';
    const vmId = connectionParams.vmId || 'sftp-session';

    const wsProtocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    const wsUrl = `${wsProtocol}//${nodeHost}:${nodePort}/api/vms/${vmId}/sftp?ip=${ip}&user=${user}&password=${encodeURIComponent(password)}`;

    console.log('[SFTP] Connecting to:', wsUrl.replace(password, '***'));

    // Close existing connection
    if (socket) {
        socket.close();
    }

    socket = new WebSocket(wsUrl);

    socket.onopen = () => {
        console.log('[SFTP] WebSocket connected');
    };

    socket.onmessage = (event) => {
        try {
            const response = JSON.parse(event.data);
            handleResponse(response);
        } catch (e) {
            console.error('[SFTP] Failed to parse response:', e);
        }
    };

    socket.onerror = (error) => {
        console.error('[SFTP] WebSocket error:', error);
        showToast('Connection error', 'error');
    };

    socket.onclose = (event) => {
        console.log('[SFTP] WebSocket closed:', event.code, event.reason);
        isConnected = false;
        updateConnectionStatus();

        if (event.code !== 1000) {
            showToast('Connection lost', 'error');
        }
    };
}

function disconnect() {
    if (socket) {
        socket.close();
        socket = null;
    }
    isConnected = false;
    files = [];
    currentPath = '/home';
    updateConnectionStatus();
    renderFileList();
    document.getElementById('connection-panel').classList.remove('hidden');
}

function updateConnectionStatus() {
    const statusDot = document.getElementById('status-dot');
    const statusText = document.getElementById('connection-status');

    if (isConnected) {
        statusDot.classList.add('connected');
        statusText.textContent = 'Connected';
        document.getElementById('connection-panel').classList.add('hidden');
    } else {
        statusDot.classList.remove('connected');
        statusText.textContent = 'Disconnected';
    }
}

// ============================================
// COMMAND HANDLING
// ============================================

function sendCommand(command) {
    if (!socket || socket.readyState !== WebSocket.OPEN) {
        showToast('Not connected', 'error');
        return;
    }
    socket.send(JSON.stringify(command));
}

function handleResponse(response) {
    console.log('[SFTP] Response:', response.type, response.success);

    switch (response.type) {
        case 'connected':
            isConnected = true;
            updateConnectionStatus();
            showToast('Connected to VM', 'success');
            // Load initial directory
            listDirectory(currentPath);
            break;

        case 'list':
            if (response.success) {
                currentPath = response.path;
                files = response.files || [];
                updateBreadcrumb();
                renderFileList();
            } else {
                showToast(response.message || 'Failed to list directory', 'error');
            }
            break;

        case 'download_start':
            handleDownloadStart(response);
            break;

        case 'download_chunk':
            handleDownloadChunk(response);
            break;

        case 'download_complete':
            handleDownloadComplete(response);
            break;

        case 'upload_ready':
            handleUploadReady(response);
            break;

        case 'upload_progress':
            handleUploadProgress(response);
            break;

        case 'upload_complete':
            handleUploadComplete(response);
            break;

        case 'mkdir':
            if (response.success) {
                showToast('Folder created', 'success');
                refreshDirectory();
            } else {
                showToast(response.message || 'Failed to create folder', 'error');
            }
            break;

        case 'delete':
            if (response.success) {
                showToast('Deleted successfully', 'success');
                refreshDirectory();
            } else {
                showToast(response.message || 'Failed to delete', 'error');
            }
            break;

        case 'rename':
            if (response.success) {
                showToast('Renamed successfully', 'success');
                refreshDirectory();
            } else {
                showToast(response.message || 'Failed to rename', 'error');
            }
            break;

        case 'error':
            showToast(response.message || 'An error occurred', 'error');
            break;

        default:
            console.log('[SFTP] Unknown response type:', response.type);
    }
}

// ============================================
// FILE OPERATIONS
// ============================================

function listDirectory(path) {
    sendCommand({ type: 'list', path });
}

function refreshDirectory() {
    listDirectory(currentPath);
}

function navigateToPath(path) {
    // Add to history
    if (currentPath !== path) {
        pathHistory = pathHistory.slice(0, historyIndex + 1);
        pathHistory.push(currentPath);
        historyIndex = pathHistory.length - 1;
    }
    listDirectory(path);
    updateNavigationButtons();
}

function navigateBack() {
    if (historyIndex > 0) {
        historyIndex--;
        listDirectory(pathHistory[historyIndex]);
        updateNavigationButtons();
    }
}

function navigateUp() {
    if (currentPath === '/') return;
    const parentPath = currentPath.split('/').slice(0, -1).join('/') || '/';
    navigateToPath(parentPath);
}

function updateNavigationButtons() {
    document.getElementById('btn-back').disabled = historyIndex <= 0;
}

function openItem(file) {
    if (file.isDirectory) {
        navigateToPath(file.path);
    } else {
        // Download file on double-click
        downloadFile(file);
    }
}

// ============================================
// DOWNLOAD HANDLING
// ============================================

function downloadFile(file) {
    if (!file || file.isDirectory) return;

    const transferId = 'dl-' + Date.now();
    activeTransfers.set(transferId, {
        type: 'download',
        path: file.path,
        name: file.name,
        size: file.size,
        received: 0,
        chunks: []
    });

    showTransferPanel();
    renderTransfers();

    sendCommand({ type: 'download', path: file.path });
}

function downloadSelected() {
    if (selectedFile && !selectedFile.isDirectory) {
        downloadFile(selectedFile);
    }
    closeContextMenu();
}

function handleDownloadStart(response) {
    const transfer = Array.from(activeTransfers.values())
        .find(t => t.type === 'download' && t.path === response.path);
    
    if (transfer) {
        transfer.totalChunks = response.totalChunks;
        transfer.chunks = [];
    }
}

function handleDownloadChunk(response) {
    const transfer = Array.from(activeTransfers.values())
        .find(t => t.type === 'download' && t.path === response.path);
    
    if (transfer) {
        transfer.chunks.push(response.chunkData);
        transfer.received = response.bytesSent;
        renderTransfers();
    }
}

function handleDownloadComplete(response) {
    const transferEntry = Array.from(activeTransfers.entries())
        .find(([_, t]) => t.type === 'download' && t.path === response.path);
    
    if (transferEntry) {
        const [transferId, transfer] = transferEntry;

        // Combine chunks and create blob
        const chunks = transfer.chunks.map(chunk => {
            const binary = atob(chunk);
            const bytes = new Uint8Array(binary.length);
            for (let i = 0; i < binary.length; i++) {
                bytes[i] = binary.charCodeAt(i);
            }
            return bytes;
        });

        const blob = new Blob(chunks);
        const url = URL.createObjectURL(blob);

        // Trigger download
        const a = document.createElement('a');
        a.href = url;
        a.download = response.fileName;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);

        // Cleanup
        activeTransfers.delete(transferId);
        renderTransfers();
        showToast(`Downloaded: ${response.fileName}`, 'success');
    }
}

// ============================================
// UPLOAD HANDLING
// ============================================

function triggerUpload() {
    document.getElementById('file-upload-input').click();
}

function handleFileSelect(event) {
    const files = event.target.files;
    if (!files.length) return;

    for (const file of files) {
        uploadFile(file);
    }

    // Reset input
    event.target.value = '';
}

function uploadFile(file) {
    if (file.size > 500 * 1024 * 1024) {
        showToast(`File too large: ${file.name} (max 500MB)`, 'error');
        return;
    }

    const transferId = 'ul-' + Date.now() + '-' + Math.random().toString(36).substr(2, 4);
    const destPath = `${currentPath}/${file.name}`.replace(/\/+/g, '/');

    activeTransfers.set(transferId, {
        type: 'upload',
        path: destPath,
        name: file.name,
        size: file.size,
        sent: 0,
        file: file,
        sessionId: null
    });

    showTransferPanel();
    renderTransfers();

    // Start upload
    sendCommand({
        type: 'upload_start',
        path: destPath,
        fileSize: file.size
    });

    // Store pending file data
    pendingChunks.set(destPath, { file, transferId });
}

function handleUploadReady(response) {
    const pending = pendingChunks.get(response.path);
    if (!pending) return;

    const { file, transferId } = pending;
    const transfer = activeTransfers.get(transferId);
    
    if (transfer) {
        transfer.sessionId = response.sessionId;
    }

    // Read and send file in chunks
    const chunkSize = 64 * 1024; // 64KB
    const reader = new FileReader();
    let offset = 0;

    const readNextChunk = () => {
        const slice = file.slice(offset, offset + chunkSize);
        reader.readAsArrayBuffer(slice);
    };

    reader.onload = (e) => {
        const chunk = e.target.result;
        const base64 = arrayBufferToBase64(chunk);

        sendCommand({
            type: 'upload_chunk',
            sessionId: response.sessionId,
            chunkData: base64
        });

        offset += chunk.byteLength;

        if (offset < file.size) {
            // Small delay to prevent overwhelming the connection
            setTimeout(readNextChunk, 10);
        } else {
            // Upload complete
            sendCommand({
                type: 'upload_complete',
                sessionId: response.sessionId
            });
            pendingChunks.delete(response.path);
        }
    };

    reader.onerror = () => {
        showToast(`Upload failed: ${file.name}`, 'error');
        activeTransfers.delete(transferId);
        pendingChunks.delete(response.path);
        renderTransfers();
    };

    readNextChunk();
}

function handleUploadProgress(response) {
    const transfer = Array.from(activeTransfers.values())
        .find(t => t.type === 'upload' && t.sessionId === response.sessionId);
    
    if (transfer) {
        transfer.sent = response.bytesReceived;
        renderTransfers();
    }
}

function handleUploadComplete(response) {
    const transferEntry = Array.from(activeTransfers.entries())
        .find(([_, t]) => t.type === 'upload' && t.path === response.path);
    
    if (transferEntry) {
        const [transferId, transfer] = transferEntry;
        activeTransfers.delete(transferId);
        renderTransfers();
        showToast(`Uploaded: ${transfer.name}`, 'success');
        refreshDirectory();
    }
}

function arrayBufferToBase64(buffer) {
    let binary = '';
    const bytes = new Uint8Array(buffer);
    for (let i = 0; i < bytes.byteLength; i++) {
        binary += String.fromCharCode(bytes[i]);
    }
    return btoa(binary);
}

// ============================================
// FOLDER OPERATIONS
// ============================================

function showNewFolderModal() {
    document.getElementById('new-folder-name').value = '';
    document.getElementById('new-folder-modal').classList.add('visible');
    document.getElementById('new-folder-name').focus();
}

function createFolder() {
    const name = document.getElementById('new-folder-name').value.trim();
    if (!name) {
        showToast('Please enter a folder name', 'error');
        return;
    }

    // Security: validate folder name
    if (name.includes('/') || name.includes('\\') || name.includes('\0')) {
        showToast('Invalid folder name', 'error');
        return;
    }

    const path = `${currentPath}/${name}`.replace(/\/+/g, '/');
    sendCommand({ type: 'mkdir', path });
    closeModal('new-folder-modal');
}

// ============================================
// DELETE OPERATIONS
// ============================================

function showDeleteModal() {
    if (!selectedFile) return;
    document.getElementById('delete-target-name').textContent = selectedFile.name;
    document.getElementById('delete-modal').classList.add('visible');
    closeContextMenu();
}

function deleteFile() {
    if (!selectedFile) return;
    sendCommand({ type: 'delete', path: selectedFile.path });
    closeModal('delete-modal');
}

// ============================================
// RENAME OPERATIONS
// ============================================

function showRenameModal() {
    if (!selectedFile) return;
    document.getElementById('rename-input').value = selectedFile.name;
    document.getElementById('rename-modal').classList.add('visible');
    document.getElementById('rename-input').focus();
    document.getElementById('rename-input').select();
    closeContextMenu();
}

function renameFile() {
    if (!selectedFile) return;
    
    const newName = document.getElementById('rename-input').value.trim();
    if (!newName) {
        showToast('Please enter a name', 'error');
        return;
    }

    if (newName.includes('/') || newName.includes('\\') || newName.includes('\0')) {
        showToast('Invalid name', 'error');
        return;
    }

    const parentPath = selectedFile.path.split('/').slice(0, -1).join('/') || '/';
    const newPath = `${parentPath}/${newName}`.replace(/\/+/g, '/');

    sendCommand({
        type: 'rename',
        path: selectedFile.path,
        newPath: newPath
    });

    closeModal('rename-modal');
}

// ============================================
// UI RENDERING
// ============================================

function renderFileList() {
    const container = document.getElementById('file-list');

    if (!isConnected) {
        container.innerHTML = `
            <div class="empty-state">
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                    <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/>
                </svg>
                <p>Connect to a VM to browse files</p>
            </div>
        `;
        return;
    }

    if (files.length === 0) {
        container.innerHTML = `
            <div class="empty-state">
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                    <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/>
                </svg>
                <p>This folder is empty</p>
            </div>
        `;
        return;
    }

    container.innerHTML = files.map(file => `
        <div class="file-item" 
             data-path="${escapeHtml(file.path)}"
             ondblclick="openItem(${escapeAttr(JSON.stringify(file))})"
             onclick="selectFile(${escapeAttr(JSON.stringify(file))})"
             oncontextmenu="showContextMenu(event, ${escapeAttr(JSON.stringify(file))})">
            <input type="checkbox" class="file-checkbox" onclick="event.stopPropagation()">
            <div class="file-name">
                ${getFileIcon(file)}
                <span class="file-name-text">${escapeHtml(file.name)}</span>
            </div>
            <span class="file-size">${file.isDirectory ? '--' : formatSize(file.size)}</span>
            <span class="file-modified">${formatDate(file.modified)}</span>
            <span class="file-permissions">${file.permissions}</span>
        </div>
    `).join('');
}

function getFileIcon(file) {
    if (file.isSymbolicLink) {
        return `<svg class="file-icon symlink" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/>
            <path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/>
        </svg>`;
    }
    
    if (file.isDirectory) {
        return `<svg class="file-icon folder" viewBox="0 0 24 24" fill="currentColor" stroke="currentColor" stroke-width="0">
            <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/>
        </svg>`;
    }

    // Determine file type icon
    const ext = file.name.split('.').pop()?.toLowerCase();
    const codeExts = ['js', 'ts', 'py', 'go', 'rs', 'c', 'cpp', 'h', 'cs', 'java', 'rb', 'php'];
    const docExts = ['txt', 'md', 'doc', 'docx', 'pdf'];
    const imgExts = ['jpg', 'jpeg', 'png', 'gif', 'svg', 'webp', 'ico'];
    const archiveExts = ['zip', 'tar', 'gz', 'rar', '7z'];

    if (codeExts.includes(ext)) {
        return `<svg class="file-icon file" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <polyline points="16 18 22 12 16 6"/>
            <polyline points="8 6 2 12 8 18"/>
        </svg>`;
    }

    if (imgExts.includes(ext)) {
        return `<svg class="file-icon file" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <rect x="3" y="3" width="18" height="18" rx="2" ry="2"/>
            <circle cx="8.5" cy="8.5" r="1.5"/>
            <polyline points="21 15 16 10 5 21"/>
        </svg>`;
    }

    if (archiveExts.includes(ext)) {
        return `<svg class="file-icon file" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <path d="M21 8v13H3V8"/>
            <path d="M1 3h22v5H1z"/>
            <path d="M10 12h4"/>
        </svg>`;
    }

    return `<svg class="file-icon file" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
        <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/>
        <polyline points="14 2 14 8 20 8"/>
    </svg>`;
}

function updateBreadcrumb() {
    const breadcrumb = document.getElementById('breadcrumb');
    const parts = currentPath.split('/').filter(Boolean);

    let html = `<span class="breadcrumb-item" onclick="navigateToPath('/')">/</span>`;

    let path = '';
    parts.forEach((part, index) => {
        path += '/' + part;
        const isLast = index === parts.length - 1;
        html += `
            <span class="breadcrumb-separator">/</span>
            <span class="breadcrumb-item ${isLast ? 'current' : ''}" 
                  ${!isLast ? `onclick="navigateToPath('${path}')"` : ''}>
                ${escapeHtml(part)}
            </span>
        `;
    });

    breadcrumb.innerHTML = html;
}

function selectFile(file) {
    selectedFile = file;
    
    // Update visual selection
    document.querySelectorAll('.file-item').forEach(el => {
        el.classList.remove('selected');
    });
    document.querySelector(`[data-path="${CSS.escape(file.path)}"]`)?.classList.add('selected');
}

function showContextMenu(event, file) {
    event.preventDefault();
    selectFile(file);

    const menu = document.getElementById('context-menu');
    menu.style.left = event.pageX + 'px';
    menu.style.top = event.pageY + 'px';
    menu.classList.add('visible');
}

function closeContextMenu() {
    document.getElementById('context-menu').classList.remove('visible');
}

// ============================================
// TRANSFER PANEL
// ============================================

function toggleTransferPanel() {
    document.getElementById('transfer-panel').classList.toggle('visible');
}

function showTransferPanel() {
    document.getElementById('transfer-panel').classList.add('visible');
}

function renderTransfers() {
    const container = document.getElementById('transfer-list');

    if (activeTransfers.size === 0) {
        container.innerHTML = `
            <div class="empty-state" style="padding: 30px 20px;">
                <p style="font-size: 13px;">No active transfers</p>
            </div>
        `;
        return;
    }

    container.innerHTML = Array.from(activeTransfers.entries()).map(([id, transfer]) => {
        const progress = transfer.size > 0 
            ? ((transfer.type === 'download' ? transfer.received : transfer.sent) / transfer.size) * 100 
            : 0;
        
        return `
            <div class="transfer-item">
                <div class="transfer-item-header">
                    <svg class="transfer-item-icon ${transfer.type}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                        ${transfer.type === 'upload' 
                            ? '<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="17 8 12 3 7 8"/><line x1="12" y1="3" x2="12" y2="15"/>'
                            : '<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" y1="15" x2="12" y2="3"/>'
                        }
                    </svg>
                    <span class="transfer-item-name">${escapeHtml(transfer.name)}</span>
                    <span class="transfer-item-size">${formatSize(transfer.size)}</span>
                </div>
                <div class="transfer-progress">
                    <div class="transfer-progress-bar" style="width: ${progress}%"></div>
                </div>
                <div class="transfer-status">${progress.toFixed(1)}% - ${transfer.type === 'download' ? 'Downloading' : 'Uploading'}...</div>
            </div>
        `;
    }).join('');
}

// ============================================
// DRAG & DROP
// ============================================

function setupDragAndDrop() {
    const dropZone = document.getElementById('drop-zone');
    let dragCounter = 0;

    document.addEventListener('dragenter', (e) => {
        e.preventDefault();
        dragCounter++;
        if (isConnected) {
            dropZone.classList.add('visible');
        }
    });

    document.addEventListener('dragleave', (e) => {
        e.preventDefault();
        dragCounter--;
        if (dragCounter === 0) {
            dropZone.classList.remove('visible');
        }
    });

    document.addEventListener('dragover', (e) => {
        e.preventDefault();
    });

    document.addEventListener('drop', (e) => {
        e.preventDefault();
        dragCounter = 0;
        dropZone.classList.remove('visible');

        if (!isConnected) return;

        const files = e.dataTransfer.files;
        for (const file of files) {
            uploadFile(file);
        }
    });
}

// ============================================
// KEYBOARD SHORTCUTS
// ============================================

function setupKeyboardShortcuts() {
    document.addEventListener('keydown', (e) => {
        // Ignore if typing in an input
        if (e.target.tagName === 'INPUT') return;

        // Delete
        if (e.key === 'Delete' && selectedFile) {
            showDeleteModal();
        }

        // Backspace - go up
        if (e.key === 'Backspace') {
            e.preventDefault();
            navigateUp();
        }

        // F2 - rename
        if (e.key === 'F2' && selectedFile) {
            showRenameModal();
        }

        // F5 - refresh
        if (e.key === 'F5') {
            e.preventDefault();
            refreshDirectory();
        }

        // Ctrl+U - upload
        if (e.ctrlKey && e.key === 'u') {
            e.preventDefault();
            triggerUpload();
        }

        // Enter - open
        if (e.key === 'Enter' && selectedFile) {
            openItem(selectedFile);
        }

        // Escape - deselect
        if (e.key === 'Escape') {
            selectedFile = null;
            document.querySelectorAll('.file-item').forEach(el => {
                el.classList.remove('selected');
            });
            closeContextMenu();
        }
    });
}

// ============================================
// MODALS & TOASTS
// ============================================

function closeModal(modalId) {
    document.getElementById(modalId).classList.remove('visible');
}

function showToast(message, type = 'info') {
    const container = document.getElementById('toast-container');
    const toast = document.createElement('div');
    toast.className = `toast ${type}`;
    toast.innerHTML = `
        <svg class="toast-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            ${type === 'success' 
                ? '<path d="M22 11.08V12a10 10 0 1 1-5.93-9.14"/><polyline points="22 4 12 14.01 9 11.01"/>'
                : '<circle cx="12" cy="12" r="10"/><line x1="12" y1="8" x2="12" y2="12"/><line x1="12" y1="16" x2="12.01" y2="16"/>'
            }
        </svg>
        <span>${escapeHtml(message)}</span>
    `;

    container.appendChild(toast);

    setTimeout(() => {
        toast.style.opacity = '0';
        toast.style.transform = 'translateX(100%)';
        setTimeout(() => toast.remove(), 200);
    }, 4000);
}

// ============================================
// UTILITIES
// ============================================

function formatSize(bytes) {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + ' ' + sizes[i];
}

function formatDate(dateStr) {
    const date = new Date(dateStr);
    return date.toLocaleDateString('en-US', {
        month: 'short',
        day: 'numeric',
        hour: '2-digit',
        minute: '2-digit'
    });
}

function escapeHtml(str) {
    if (!str) return '';
    return str
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;');
}

function escapeAttr(str) {
    return str.replace(/'/g, "\\'").replace(/"/g, '&quot;');
}
