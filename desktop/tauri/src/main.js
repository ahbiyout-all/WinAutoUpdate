import "./styles.css";

const output = document.querySelector("#output");

document.querySelector("#ping").addEventListener("click", () => {
  output.textContent = "Tauri 데스크톱 셸이 정상적으로 로드되었습니다.";
});
