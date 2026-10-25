(() => {
  const form = document.getElementById('post-editor');
  if (!form) return;
  const text = document.getElementById('Input_Markdown');
  const writeTab = document.getElementById('write-tab');
  const previewTab = document.getElementById('preview-tab');
  const writePanel = document.getElementById('write-panel');
  const previewPanel = document.getElementById('preview-panel');
  const status = document.getElementById('preview-status');
  const save = document.getElementById('save-post');
  let dirty = false;
  let pending = false;

  const selectTab = preview => {
    writeTab.setAttribute('aria-selected', String(!preview));
    previewTab.setAttribute('aria-selected', String(preview));
    writePanel.hidden = preview;
    previewPanel.hidden = !preview;
    text.required = !preview;
  };
  writeTab.addEventListener('click', event => { event.preventDefault(); selectTab(false); });
  previewTab.addEventListener('click', async event => {
    event.preventDefault();
    if (pending) return;
    pending = true;
    previewTab.disabled = true;
    status.textContent = 'Loading preview…';
    try {
      const body = new FormData(form);
      body.delete('Banner');
      body.delete('Attachment');
      const response = await fetch(previewTab.formAction, {
        method: 'POST', body, credentials: 'same-origin',
        headers: { 'X-Requested-With': '7blog-preview' }
      });
      if (!response.ok || response.headers.get('X-7blog-Preview') !== 'true') throw new Error('Preview unavailable');
      document.getElementById('markdown-preview').innerHTML = await response.text();
      selectTab(true);
      status.textContent = 'Preview · not saved';
    } catch {
      status.textContent = 'Could not load preview. Check your connection and sign-in session.';
    } finally {
      pending = false;
      previewTab.disabled = false;
    }
  });
  for (const tab of [writeTab, previewTab]) {
    tab.addEventListener('keydown', event => {
      if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
      event.preventDefault();
      const next = tab === writeTab ? previewTab : writeTab;
      next.focus();
      next.click();
    });
  }

  // Images: upload in the background and insert the Markdown line at the cursor.
  const attachment = document.getElementById('Attachment');
  const uploadButton = document.getElementById('upload-attachment');
  const attachmentList = document.getElementById('attachment-list');
  const insertBlock = markdown => {
    if (writePanel.hidden) selectTab(false);
    const start = text.selectionStart;
    const before = text.value.slice(0, start);
    const after = text.value.slice(text.selectionEnd);
    // Keep the image on its own paragraph: exactly one blank line on each side.
    const lead = !before || before.endsWith('\n\n') ? '' : before.endsWith('\n') ? '\n' : '\n\n';
    const trail = after.startsWith('\n\n') ? '' : after.startsWith('\n') ? '\n' : '\n\n';
    const block = lead + markdown + trail;
    if (text.value.length - (text.selectionEnd - start) + block.length > text.maxLength) return;
    text.setRangeText(block, start, text.selectionEnd, 'end');
    text.focus();
    text.dispatchEvent(new Event('input', { bubbles: true }));
  };
  const addToList = (url, markdown) => {
    const item = document.createElement('li');
    const image = document.createElement('img');
    image.src = url; image.alt = '';
    const code = document.createElement('code');
    code.textContent = markdown;
    const button = document.createElement('button');
    button.type = 'button'; button.dataset.insert = markdown; button.textContent = 'Insert';
    item.append(image, code, button);
    attachmentList.append(item);
  };
  const upload = async files => {
    for (const file of files) {
      if (!file.type.startsWith('image/')) continue;
      const body = new FormData();
      body.append('__RequestVerificationToken', form.querySelector('input[name="__RequestVerificationToken"]').value);
      body.append('Attachment', file, file.name || 'image.png');
      status.textContent = `Uploading ${file.name || 'image'}…`;
      try {
        const response = await fetch(uploadButton.formAction, {
          method: 'POST', body, credentials: 'same-origin', headers: { 'X-Requested-With': '7blog-upload' }
        });
        const result = await response.json().catch(() => ({}));
        if (!response.ok) throw new Error(result.error || 'The image could not be uploaded.');
        insertBlock(result.markdown);
        addToList(result.url, result.markdown);
        status.textContent = 'Image inserted · unsaved changes';
      } catch (error) {
        status.textContent = error.message || 'The image could not be uploaded.';
      }
    }
  };
  uploadButton.hidden = true;
  attachment.addEventListener('change', async () => {
    await upload([...attachment.files]);
    attachment.value = '';
  });
  attachmentList.addEventListener('click', event => {
    const button = event.target.closest('button[data-insert]');
    if (button) insertBlock(button.dataset.insert);
  });
  for (const button of attachmentList.querySelectorAll('button[data-insert]')) button.hidden = false;
  text.addEventListener('paste', event => {
    const files = [...(event.clipboardData?.files ?? [])].filter(file => file.type.startsWith('image/'));
    if (files.length === 0) return;
    event.preventDefault();
    upload(files);
  });
  text.addEventListener('dragover', event => {
    if (event.dataTransfer?.types.includes('Files')) event.preventDefault();
  });
  text.addEventListener('drop', event => {
    const files = [...(event.dataTransfer?.files ?? [])].filter(file => file.type.startsWith('image/'));
    if (files.length === 0) return;
    event.preventDefault();
    upload(files);
  });

  const toolbar = form.querySelector('.editor-toolbar');
  toolbar.hidden = false;
  toolbar.addEventListener('click', event => {
    const button = event.target.closest('button[data-format]');
    if (!button) return;
    if (button.dataset.format === 'image') { attachment.click(); return; }
    const start = text.selectionStart;
    const end = text.selectionEnd;
    const selected = text.value.slice(start, end);
    const formats = {
      bold: `**${selected || 'bold text'}**`, italic: `*${selected || 'italic text'}*`,
      heading: `\n## ${selected || 'Heading'}\n`, link: `[${selected || 'link text'}](https://example.com)`,
      list: '\n' + (selected || 'List item').split('\n').map(line => `- ${line}`).join('\n') + '\n',
      code: selected.includes('\n') ? `\n\`\`\`\n${selected}\n\`\`\`\n` : `\`${selected || 'code'}\``
    };
    const replacement = formats[button.dataset.format];
    if (text.value.length - (end - start) + replacement.length > text.maxLength) return;
    text.setRangeText(replacement, start, end, 'select');
    text.focus();
    text.dispatchEvent(new Event('input', { bubbles: true }));
  });
  form.addEventListener('input', () => {
    dirty = true;
    const words = text.value.trim() ? text.value.trim().split(/\s+/).length : 0;
    document.getElementById('editor-word-count').textContent = `${words} words`;
    document.getElementById('slug-preview').textContent = document.getElementById('Input_Slug').value || 'your-post';
    status.textContent = 'Unsaved changes';
  });
  form.addEventListener('submit', event => {
    if (event.submitter === save) dirty = false;
  });
  document.addEventListener('keydown', event => {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
      event.preventDefault();
      if (!pending) form.requestSubmit(save);
    }
  });
  window.addEventListener('beforeunload', event => {
    if (dirty) { event.preventDefault(); event.returnValue = ''; }
  });
})();
