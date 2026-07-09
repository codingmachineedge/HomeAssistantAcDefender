// Wires up the in-document search panels embedded in the wiki markdown
// (Algorithms.md, Every-Guard-Explained.md, Defender-Logic.md use data-search-* attributes).
// Bound once, globally, via event delegation so it survives Blazor re-renders.
window.acWiki = (function () {
    let bound = false;

    function filter(root) {
        const input = root.querySelector('[data-search-input]');
        if (!input) {
            return;
        }
        const q = (input.value || '').trim().toLowerCase();
        let shown = 0;
        root.querySelectorAll('[data-search-item]').forEach(function (el) {
            const text = (el.getAttribute('data-search-text') || el.textContent || '').toLowerCase();
            const match = !q || text.indexOf(q) !== -1;
            el.style.display = match ? '' : 'none';
            if (match) {
                shown++;
            }
        });
        const count = root.querySelector('[data-search-count]');
        if (count) {
            count.textContent = shown;
        }
        const empty = root.querySelector('[data-search-empty]');
        if (empty) {
            if (shown === 0) {
                empty.removeAttribute('hidden');
            } else {
                empty.setAttribute('hidden', '');
            }
        }
    }

    function bind() {
        if (bound) {
            return;
        }
        bound = true;
        document.addEventListener('input', function (e) {
            const input = e.target.closest && e.target.closest('[data-search-input]');
            if (input) {
                const root = input.closest('[data-search-root]');
                if (root) {
                    filter(root);
                }
            }
        });
        document.addEventListener('click', function (e) {
            const clear = e.target.closest && e.target.closest('[data-search-clear]');
            if (clear) {
                const root = clear.closest('[data-search-root]');
                if (root) {
                    const input = root.querySelector('[data-search-input]');
                    if (input) {
                        input.value = '';
                        filter(root);
                    }
                }
            }
        });
    }

    return { bind: bind };
})();
