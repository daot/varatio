(function () {
  console.log("VARatio server-side stream script loaded");

  function getVideoElement() {
    return (
      document.querySelector(".htmlVideoplayer") ||
      document.querySelector("video")
    );
  }

  async function hasVarData(itemId) {
    try {
      const headers = {};
      if (
        window.ApiClient &&
        typeof window.ApiClient.accessToken === "function"
      ) {
        headers["Authorization"] =
          'MediaBrowser Token="' + window.ApiClient.accessToken() + '"';
      }

      const response = await fetch("/VARatio/Data?itemId=" + itemId, {
        method: "GET",
        headers,
      });

      return response.ok;
    } catch (e) {
      console.error("VARatio: Failed to query VAR data", e);
      return false;
    }
  }

  async function switchToVarStream(itemId) {
    const video = getVideoElement();
    if (!video) {
      return;
    }

    const ok = await hasVarData(itemId);
    if (!ok) {
      return;
    }

    // Switch the player to our server-side cropped stream.
    const streamUrl = "/VARatio/Stream?itemId=" + itemId;

    try {
      console.log("VARatio: Switching video source to", streamUrl);

      // Pause current playback managed by Jellyfin.
      video.pause();

      // Replace the source and restart playback.
      video.src = streamUrl;
      video.load();
      await video.play();
    } catch (e) {
      console.error("VARatio: Failed to switch to VAR stream", e);
    }
  }

  document.addEventListener("playbackstart", function (e) {
    try {
      if (!e.detail || !e.detail.item) {
        return;
      }

      const itemId = e.detail.item.Id;
      if (!itemId) {
        return;
      }

      // Fire and forget; we don't block Jellyfin's own playback logic here.
      switchToVarStream(itemId);
    } catch (err) {
      console.error("VARatio: playbackstart handler error", err);
    }
  });
})();

