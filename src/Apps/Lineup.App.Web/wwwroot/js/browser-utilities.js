function downloadTextFile(fileName, contentType, content) {
    const blob = new Blob([content], { type: contentType });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    link.style.display = 'none';
    document.body.appendChild(link);
    try {
        link.click();
    } finally {
        link.remove();
        URL.revokeObjectURL(url);
    }
}

async function copyTextToClipboard(content) {
    if (navigator.clipboard?.writeText) {
        try {
            await navigator.clipboard.writeText(content);
            return;
        } catch {
            // LAN HTTP origins and browser permission policies can reject the modern API.
        }
    }

    const textArea = document.createElement('textarea');
    textArea.value = content;
    textArea.setAttribute('readonly', '');
    textArea.style.position = 'fixed';
    textArea.style.left = '-9999px';
    textArea.style.opacity = '0';
    document.body.appendChild(textArea);

    const activeElement = document.activeElement;
    try {
        textArea.focus();
        textArea.select();
        textArea.setSelectionRange(0, textArea.value.length);
        if (!document.execCommand('copy')) {
            throw new Error('The browser rejected both clipboard copy methods.');
        }
    } finally {
        textArea.remove();
        if (activeElement instanceof HTMLElement) {
            activeElement.focus();
        }
    }
}
