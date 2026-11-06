// TV Guide resize observer
window.guideResizeObserver = null;
window.guideDotNetRef = null;


function setupGuideResizeObserver(dotNetRef) {
    // Clean up any existing observer
    disposeGuideResizeObserver();
    
    window.guideDotNetRef = dotNetRef;
    
    // Observe the main content area, not the guide container itself
    // The guide container can grow, but we want to know the available space
    const mainContent = document.querySelector('main') || document.querySelector('article') || document.body;
    
    window.guideResizeObserver = new ResizeObserver(entries => {
        for (let entry of entries) {
            // Calculate available width (main content width minus some padding)
            const width = Math.floor(entry.contentRect.width) - 40;
            if (window.guideDotNetRef && width > 0) {
                window.guideDotNetRef.invokeMethodAsync('OnContainerResize', width);
            }
        }
    });
    
    window.guideResizeObserver.observe(mainContent);
}

function getGuideViewportWidth() {
    return (document.querySelector('main') || document.querySelector('article') || document.body).clientWidth - 40;
}


function disposeGuideResizeObserver() {
    if (window.guideResizeObserver) {
        window.guideResizeObserver.disconnect();
        window.guideResizeObserver = null;
    }
    window.guideDotNetRef = null;
}
