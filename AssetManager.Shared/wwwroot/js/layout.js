// Layout helpers for MainLayout (design D3).

// The page content scrolls in .mud-main-content, not the document, so Blazor's own scroll-to-top on navigation
// does not reach it.
export function scrollContentToTop() {
    const main = document.querySelector('.mud-main-content');
    if (main) main.scrollTop = 0;
}

// Brings an element (a Settings section) to the top of the content scroll container.
export function scrollIntoView(id) {
    document.getElementById(id)?.scrollIntoView({ block: 'start', behavior: 'smooth' });
}
