import {MistralClient} from "@mistralai/mistralai";
import {getConfigVariable} from "./util.js";

export default class MistralService {
    #mistral;
    #model = "mistral-tiny";

    constructor() {
        const apiKey = getConfigVariable("MISTRAL_API_KEY")

        this.#mistral = new MistralClient(apiKey);
    }

    async classify(allLists, destinationName, description) {
        try {
            const prompt = `Categorize this transaction from my bank account with the following 
        description ${description} and the following destination ${destinationName}`;

            const categories = allLists.get('categories');
            const budgets = allLists.get('budgets');

            const response = await this.#mistral.chat({
                model: this.#model,
                messages: [{role: "user", content: prompt}],
                tools: [
                    {
                        "type": "function",
                        "function": {
                            "name": "classification",
                            "description": "Classify a financial transaction into a category and budget, use only values from the lists provided.",
                            "parameters": {
                                "type": "object",
                                "properties": {
                                    "category": {
                                        "type": "string",
                                        "description": `The category to classify the transaction into. 
                                    Use only these values: ${categories.join(", ")}.
                                    Use none if no category applies.`
                                    },
                                    "budget": {
                                        "type": "string",
                                        "description": `The budget to classify the transaction into.
                                    Use only these values: ${budgets.join(", ")}.
                                    Use none if no budget applies.
                                    `
                                    }
                                },
                                "required": ["category", "budget"]
                            }
                        }
                    }
                ],
                tool_choice: {"type": "function", "function": {"name": "classification"}},
            });

            const function_call = response.choices[0].message.tool_calls[0];
            const json = JSON.parse(function_call.function.arguments);

            if (categories.indexOf(json.category) === -1 && budgets.indexOf(json.budget) === -1) {
                console.warn(`Mistral could not classify the transaction. 
                Prompt: ${prompt}
                Mistral's guess: ${function_call.function.arguments}`)
                return null;
            }

            return {
                prompt,
                response: function_call.function.arguments,
                category: json.category,
                budget: json.budget
            }

        } catch (error) {
            if (error.response) {
                console.error(error.response.status);
                console.error(error.response.data);
                throw new MistralException(error.status, error.response, error.response.data);
            } else {
                console.error(error.message);
                throw new MistralException(null, null, error.message);
            }
        }
    }
}

class MistralException extends Error {
    code;
    response;
    body;

    constructor(statusCode, response, body) {
        super(`Error while communicating with Mistral: ${statusCode} - ${body}`);

        this.code = statusCode;
        this.response = response;
        this.body = body;
    }
}