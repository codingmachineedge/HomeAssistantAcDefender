window.acAccessibility = {
    previousFocus: null,
    modalTrap: null,
    modalBackgrounds: [],

    setDocumentLanguage(language) {
        document.documentElement.lang = language || "en";
    },

    captureFocus() {
        this.previousFocus = document.activeElement instanceof HTMLElement
            ? document.activeElement
            : null;
    },

    activateModal(dialog) {
        if (!(dialog instanceof HTMLElement)) return;
        if (!this.previousFocus && document.activeElement instanceof HTMLElement) {
            this.previousFocus = document.activeElement;
        }

        this.restoreModalBackgrounds();
        let modalBranch = dialog;
        let parent = dialog.parentElement;
        while (parent) {
            for (const sibling of parent.children) {
                if (sibling === modalBranch || !(sibling instanceof HTMLElement)) continue;
                this.modalBackgrounds.push({
                    element: sibling,
                    hadInert: sibling.hasAttribute('inert'),
                    inertValue: sibling.getAttribute('inert'),
                    hadAriaHidden: sibling.hasAttribute('aria-hidden'),
                    ariaHiddenValue: sibling.getAttribute('aria-hidden')
                });
                sibling.setAttribute('inert', '');
                sibling.setAttribute('aria-hidden', 'true');
            }
            modalBranch = parent;
            parent = parent.parentElement;
        }

        const focusableSelector = 'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';
        const trap = event => {
            if (event.key !== 'Tab') return;
            const focusable = Array.from(dialog.querySelectorAll(focusableSelector))
                .filter(element => !element.hasAttribute('hidden') && element.getClientRects().length > 0);
            if (focusable.length === 0) {
                event.preventDefault();
                dialog.focus();
                return;
            }

            const first = focusable[0];
            const last = focusable[focusable.length - 1];
            if (event.shiftKey && document.activeElement === first) {
                event.preventDefault();
                last.focus();
            } else if (!event.shiftKey && document.activeElement === last) {
                event.preventDefault();
                first.focus();
            }
        };

        if (this.modalTrap) dialog.removeEventListener('keydown', this.modalTrap);
        this.modalTrap = trap;
        dialog.addEventListener('keydown', trap);
        const first = dialog.querySelector(focusableSelector);
        (first || dialog).focus({ preventScroll: true });
    },

    deactivateModal(dialog) {
        if (dialog instanceof HTMLElement && this.modalTrap) {
            dialog.removeEventListener('keydown', this.modalTrap);
        }
        this.modalTrap = null;
        this.restoreModalBackgrounds();
        const canRestorePreviousFocus = this.previousFocus instanceof HTMLElement
            && document.contains(this.previousFocus)
            && !this.previousFocus.matches(':disabled, [aria-disabled="true"]')
            && !this.previousFocus.closest('[inert], [aria-hidden="true"]')
            && this.previousFocus.getClientRects().length > 0;
        if (canRestorePreviousFocus) {
            this.previousFocus.focus({ preventScroll: true });
        } else {
            const fallback = document.getElementById('main-content');
            if (fallback instanceof HTMLElement) fallback.focus({ preventScroll: true });
        }
        this.previousFocus = null;
    },

    restoreModalBackgrounds() {
        for (const state of this.modalBackgrounds) {
            if (!(state.element instanceof HTMLElement)) continue;
            if (state.hadInert) {
                state.element.setAttribute('inert', state.inertValue ?? '');
            } else {
                state.element.removeAttribute('inert');
            }
            if (state.hadAriaHidden) {
                state.element.setAttribute('aria-hidden', state.ariaHiddenValue ?? '');
            } else {
                state.element.removeAttribute('aria-hidden');
            }
        }
        this.modalBackgrounds = [];
    }
};
