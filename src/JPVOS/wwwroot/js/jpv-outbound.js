window.jpvOutbound = {
  sendConnorMessage: async function (body) {
    const response = await fetch('/api/outbound/conversation/connor/messages', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ body }),
      redirect: 'follow'
    });
    if (response.redirected || response.status === 401 || response.status === 403) {
      return { success: false, error: 'session_reauthorization_required' };
    }
    let payload = null;
    try { payload = await response.json(); } catch { }
    if (!response.ok) return { success: false, error: payload?.error ?? `http_${response.status}` };
    if (!payload?.messageId) return { success: false, error: 'authorized_send_receipt_missing' };
    return { success: true, messageId: payload.messageId };
  }
};
