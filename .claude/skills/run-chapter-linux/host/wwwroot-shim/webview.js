/*
 * Stands in for the WebView2 host object on Linux.
 *
 * WebView2 gives the page `window.chrome.webview` with a fire-and-forget pipe in each
 * direction. This reproduces exactly that surface on top of a WebSocket, so
 * src/bridge.ts is used unmodified: it still posts objects and still receives events
 * whose `data` is an already-parsed object, which is what PostWebMessageAsJson does.
 */
(function () {
  const listeners = []
  const queue = []
  const url = (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/bridge'
  let socket = null

  function connect() {
    socket = new WebSocket(url)

    socket.onopen = function () {
      while (queue.length) socket.send(queue.shift())
    }

    socket.onmessage = function (event) {
      let data
      try {
        data = JSON.parse(event.data)
      } catch (error) {
        console.error('[shim] unparseable host message', error)
        return
      }
      for (const listener of listeners) listener({ data: data })
    }

    socket.onclose = function () {
      console.warn('[shim] host connection closed')
    }
  }

  window.chrome = window.chrome || {}
  window.chrome.webview = {
    postMessage: function (message) {
      const text = JSON.stringify(message)
      if (socket && socket.readyState === WebSocket.OPEN) socket.send(text)
      else queue.push(text)
    },
    addEventListener: function (type, listener) {
      if (type === 'message') listeners.push(listener)
    },
  }

  connect()
})()
