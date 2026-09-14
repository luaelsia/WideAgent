// Claude 데스크톱 대화창 폭 넓히기
// DevTools > Sources > Snippets 에 'wide-chat' 이라는 이름으로 저장해서 사용
// 실행: DevTools에서 Ctrl+Shift+P -> "!wide-chat" -> Enter

(() => {
  const MODE  = 'l';   // 앱 내장 폭 단계: 's'(기본,840px) / 'm' / 'l'(1280px)
  const WIDTH = '';    // 1280px보다 더 넓게: '2000px' 또는 'min(2400px,80vw)'. 비우면 내장값 사용

  const root = document.documentElement;
  const apply = () => {
    if (root.dataset.transcriptWidth !== MODE) root.dataset.transcriptWidth = MODE;
  };
  apply();

  // 앱이 다시 그리면서 기본값으로 되돌리는 것을 막는다
  new MutationObserver(apply).observe(root, {
    attributes: true,
    attributeFilter: ['data-transcript-width'],
  });

  document.getElementById('wide-chat')?.remove();
  if (WIDTH) {
    const s = document.createElement('style');
    s.id = 'wide-chat';
    s.textContent = `*{--max-content-width:${WIDTH} !important}`;
    document.head.appendChild(s);
  }

  return 'wide-chat applied: ' + MODE + (WIDTH ? ' @ ' + WIDTH : ' (1280px)');
})()
