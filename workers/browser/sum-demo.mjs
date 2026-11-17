const values = [1, 2, 3, 4, 5];
const sum = values.reduce((total, value) => total + value, 0);
console.log(`values: ${values.join(", ")}`);
console.log(`sum: ${sum}`);
if (sum !== 15) process.exitCode = 1;
