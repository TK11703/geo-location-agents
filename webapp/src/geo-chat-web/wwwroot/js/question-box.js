// A textarea has no intrinsic height, so the box is grown to its own content on every change.
function resize(textarea) {
    textarea.style.height = 'auto';
    textarea.style.height = `${textarea.scrollHeight}px`;
}

export function connect(textarea, owner) {
    resize(textarea);

    textarea.addEventListener('input', () => resize(textarea));

    textarea.addEventListener('keydown', event => {
        // Enter asks; Shift+Enter is how a question gets a second line.
        if (event.key !== 'Enter' || event.shiftKey) {
            return;
        }

        event.preventDefault();

        const question = textarea.value.trim();
        if (question.length === 0) {
            return;
        }

        // The text is read and cleared here rather than data-bound, because anything typed before
        // the circuit connects never reaches the server.
        textarea.value = '';
        resize(textarea);

        owner.invokeMethodAsync('AskAsync', question);
    });
}
